using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.AI.Providers.OpenAiCompatible;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Notifications.Providers.Meta;

/// <summary>
/// Fatia de documento seguro (#272 PR2) com extração tipada (#272 PR3):
/// baixa o documento admitido da Meta, valida MIME/bytes/páginas, faz o scan
/// antimalware, armazena em quarentena privada e extrai o candidato tipado
/// de fatura (`candidate` ou `needs_review`, sem defaults silenciosos, sem
/// arredondamento e sem mutação Pagaí) antes de registrar a admissão
/// durável. Sem resposta automática. Falha fechada com motivos estáveis.
/// </summary>
public sealed class InboundWhatsAppDocumentService(
    FoundationDbContext database,
    IHttpClientFactory httpClientFactory,
    MetaWhatsAppOptions metaOptions,
    InboundWhatsAppAiOptions aiOptions,
    FileStorage storage,
    IMalwareScanner malwareScanner,
    IOptions<MalwareScannerOptions> malwareScannerOptions,
    ILogger<InboundWhatsAppDocumentService> logger
)
{
    private const string Operation = "whatsapp_inbound_document";
    private const string Module = "notifications";

    private static readonly DocumentContextLimits PdfLimits = new();
    private static readonly System.Text.RegularExpressions.Regex PaguaiAmountGrammar = new(
        "^[0-9]+(\\.[0-9]+)?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
    );
    private static readonly HashSet<string> AllowedMimeTypes = new(
        ["application/pdf", "image/jpeg", "image/png"],
        StringComparer.Ordinal
    );

    public async Task ProcessAsync(Guid inboundMessageId, CancellationToken cancellationToken)
    {
        var claim = await ClaimAsync(inboundMessageId, cancellationToken);
        if (claim is null)
        {
            return;
        }

        if (!await ActorIsAllowedAsync(inboundMessageId, cancellationToken))
        {
            // Membership revogada ou capability ausente já no início: nada é
            // baixado, escaneado ou extraído para este remetente.
            await FailLoggedAsync(inboundMessageId, "capability_missing");
            return;
        }

        MetaWhatsAppMedia media;
        int? pageCount;
        try
        {
            media = await DownloadAsync(claim.MediaId, cancellationToken);
            pageCount = ValidateMedia(media, cancellationToken);
        }
        catch (InboundWhatsAppPermanentException exception)
            when (exception.Message.Contains("byte limit", StringComparison.Ordinal))
        {
            await FailLoggedAsync(inboundMessageId, "document_rejected");
            return;
        }
        catch (InboundWhatsAppPermanentException)
        {
            // Documento rejeitado/expirado no Graph: terminal para esta
            // mensagem (o usuário reenvia se ainda for relevante).
            await FailLoggedAsync(inboundMessageId, "media_unavailable");
            return;
        }
        catch (DocumentContextValidationException)
        {
            // PDF malformado ou acima do teto de páginas aprovado.
            await FailLoggedAsync(inboundMessageId, "document_rejected");
            return;
        }

        var verdict = await ScanAsync(inboundMessageId, media, cancellationToken);
        if (verdict.Status != MalwareScanStatus.Clean)
        {
            await FailLoggedAsync(
                inboundMessageId,
                verdict.Status == MalwareScanStatus.MalwareDetected
                    ? "document_rejected"
                    : "document_scan_unavailable"
            );
            return;
        }

        var providerKey =
            $"quarantine/whatsapp/{claim.ResolvedCompanyId:N}/{inboundMessageId:N}";
        try
        {
            await storage.EnsurePrivateContainerAsync(cancellationToken);
            await storage.UploadObjectAsync(providerKey, media.Bytes, media.MediaType, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Storage não configurado (BlockedFileStorage) ou rejeitado pelo
            // provider: falha fechada sem estado parcial.
            await FailLoggedAsync(inboundMessageId, "document_storage_unavailable");
            return;
        }
        catch (HttpRequestException)
        {
            throw new NotificationProviderTransientException(
                "Document storage upload failed transiently."
            );
        }

        // Extração tipada (#272 PR3): needs_review não é falha da mensagem —
        // o documento admitido carrega o resultado fechado da extração.
        var candidate = await ExtractCandidateAsync(media, claim.Caption, cancellationToken);
        await RecordCandidateAsync(inboundMessageId, candidate, cancellationToken);

        var completion = await CompleteAsync(
            inboundMessageId,
            media.MediaType,
            media.Bytes.LongLength,
            ChecksumOf(media.Bytes),
            pageCount,
            verdict,
            providerKey,
            Guid.NewGuid(),
            cancellationToken
        );
        if (completion != "completed")
        {
            // Membership revogada ou capability ausente no momento do
            // registro: a admissão não vira documento da empresa.
            await FailLoggedAsync(inboundMessageId, "capability_missing");
        }
    }

    private sealed record DocumentClaim(
        string MediaId,
        string? Caption,
        Guid ResolvedCompanyId
    );

    private async Task<DocumentClaim?> ClaimAsync(Guid inboundMessageId, CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT status, claimed, media_id, caption, resolved_company_id FROM "
            + "notifications.claim_inbound_whatsapp_document(@inbound_id)";
        var inboundId = command.CreateParameter();
        inboundId.ParameterName = "inbound_id";
        inboundId.Value = inboundMessageId;
        command.Parameters.Add(inboundId);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(1))
            {
                // Replay, estado terminal ou mensagem que não é documento
                // admitido: sem trabalho, sem erro.
                logger.LogInformation(
                    "Inbound document {Operation} {Module} {Result} {Reason}",
                    Operation,
                    Module,
                    "skipped",
                    "not_claimable"
                );
                return null;
            }

            return new DocumentClaim(
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetGuid(4)
            );
        }
        catch (PostgresException exception)
            when (
                exception.SqlState
                is PostgresErrorCodes.InvalidParameterValue
                    or PostgresErrorCodes.CheckViolation
            )
        {
            throw new InboundWhatsAppPermanentException(
                "Inbound document claim was rejected."
            );
        }
    }

    private async Task<MetaWhatsAppMedia> DownloadAsync(string mediaId, CancellationToken cancellationToken)
    {
        var mediaClient = new MetaWhatsAppMediaClient(
            httpClientFactory.CreateClient(MetaWhatsAppClient.HttpClientName),
            metaOptions,
            logger
        );
        return await mediaClient.DownloadAsync(
            mediaId,
            DocumentUploadPolicy.MaxSizeBytes,
            cancellationToken
        );
    }

    /// <summary>Valida MIME allowlist; PDF exige parse e teto de páginas aprovado.</summary>
    private static int? ValidateMedia(MetaWhatsAppMedia media, CancellationToken cancellationToken)
    {
        if (!AllowedMimeTypes.Contains(media.MediaType))
        {
            // Rejeição de validação (mesma classe do pipeline de contexto):
            // mapeia para o motivo estável document_rejected.
            throw new DocumentContextValidationException("document media type is not allowed");
        }

        if (media.MediaType == "application/pdf")
        {
            using var stream = new MemoryStream(media.Bytes);
            var pages = DocumentTextExtractor.Extract(stream, PdfLimits, cancellationToken);
            return pages.Count > 0 ? pages.Count : null;
        }

        return null;
    }

    private static string ChecksumOf(byte[] bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

    private async Task<MalwareScanVerdict> ScanAsync(
        Guid inboundMessageId,
        MetaWhatsAppMedia media,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(malwareScannerOptions.Value.Timeout);
        try
        {
            return await malwareScanner.ScanAsync(
                new MalwareScanRequest(
                    $"whatsapp/{inboundMessageId:N}",
                    _ => Task.FromResult<Stream>(new MemoryStream(media.Bytes))
                ),
                timeout.Token
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MalwareScanVerdict(
                MalwareScanStatus.Unavailable,
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                null,
                "scan_timeout"
            );
        }
    }

    private async Task<string> CompleteAsync(
        Guid inboundMessageId,
        string mimeType,
        long sizeBytes,
        string checksum,
        int? pageCount,
        MalwareScanVerdict verdict,
        string providerKey,
        Guid correlationId,
        CancellationToken cancellationToken
    )
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT notifications.complete_inbound_whatsapp_document("
            + "@inbound_id, @mime_type, @size_bytes, @checksum, @page_count, "
            + "@scan_engine, @scan_engine_version, @scan_signature_version, "
            + "@scanned_at, @provider_key, @correlation_id)";
        AddParameter(command, "inbound_id", inboundMessageId);
        AddParameter(command, "mime_type", mimeType);
        AddParameter(command, "size_bytes", sizeBytes);
        AddParameter(command, "checksum", checksum);
        AddParameter(command, "page_count", pageCount.HasValue ? pageCount.Value : DBNull.Value);
        AddParameter(command, "scan_engine", verdict.Engine is null ? DBNull.Value : verdict.Engine);
        AddParameter(
            command,
            "scan_engine_version",
            verdict.EngineVersion is null ? DBNull.Value : verdict.EngineVersion
        );
        AddParameter(
            command,
            "scan_signature_version",
            verdict.SignatureVersion is null ? DBNull.Value : verdict.SignatureVersion
        );
        AddParameter(command, "scanned_at", verdict.ScannedAtUtc);
        AddParameter(command, "provider_key", providerKey);
        AddParameter(command, "correlation_id", correlationId);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task FailLoggedAsync(Guid inboundMessageId, string reason)
    {
        logger.LogWarning(
            "Inbound document {Operation} {Module} {Result} {Reason}",
            Operation,
            Module,
            "failed",
            reason
        );
        var connection = await OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT notifications.fail_inbound_whatsapp_message(@inbound_id, @failure_reason)";
        AddParameter(command, "inbound_id", inboundMessageId);
        AddParameter(command, "failure_reason", reason);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private sealed record InvoiceCandidate(
        string State,
        string? BeneficiaryName,
        string? Amount,
        string? DueDate,
        string? DocumentNumber,
        string? NeedsReviewReason
    );

    /// <summary>
    /// Extração fechada: PDF usa a camada de texto local (sem camada =
    /// needs_review); imagem passa pelo OCR de visão. Sem perfis de IA o
    /// resultado é needs_review — o documento permanece admitido.
    /// </summary>
    private async Task<InvoiceCandidate> ExtractCandidateAsync(
        MetaWhatsAppMedia media,
        string? caption,
        CancellationToken cancellationToken
    )
    {
        string? text;
        if (media.MediaType == "application/pdf")
        {
            using var stream = new MemoryStream(media.Bytes);
            var pages = DocumentTextExtractor.Extract(stream, PdfLimits, cancellationToken);
            text = string.Join(
                '\n',
                pages.Where(page => page.HasTextLayer && page.Text.Length > 0).Select(page => page.Text)
            );
            if (string.IsNullOrWhiteSpace(text))
            {
                return NeedsReview("no_text_layer");
            }
        }
        else
        {
            if (!aiOptions.IsConfigured)
            {
                return NeedsReview("extraction_unavailable");
            }

            var limits = aiOptions.CreateDocumentLimits();
            var canonical = ImageCanonicalizer.Canonicalize(media.Bytes, limits);
            var vision = new DocumentVisionOcr(
                new OpenAiCompatibleChatClient(
                    aiOptions.Vision,
                    httpClientFactory.CreateClient(InboundWhatsAppInteractionService.VisionHttpClientName)
                )
            );
            var page = await vision.TranscribePageAsync(canonical.Bytes, canonical.MediaType, cancellationToken);
            text = page.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return NeedsReview("no_text_layer");
            }
        }

        if (!aiOptions.IsConfigured)
        {
            return NeedsReview("extraction_unavailable");
        }

        var extractor = new InboundInvoiceExtractor(
            new OpenAiCompatibleChatClient(
                aiOptions.Text,
                httpClientFactory.CreateClient(InboundWhatsAppInteractionService.TextHttpClientName)
            )
        );
        ExtractedInvoiceData data;
        try
        {
            data = await extractor.ExtractAsync(text!, caption, cancellationToken);
        }
        catch (InboundWhatsAppPermanentException)
        {
            return NeedsReview("extraction_unavailable");
        }

        return CloseCandidate(data);

        InvoiceCandidate NeedsReview(string reason) =>
            new("needs_review", null, null, null, null, reason);
    }

    private static InvoiceCandidate CloseCandidate(ExtractedInvoiceData data)
    {
        var amount = data.Amount is { Length: > 0 } ? data.Amount.Trim() : null;
        var beneficiary = data.BeneficiaryName is { Length: > 0 } ? data.BeneficiaryName.Trim() : null;
        var dueDate = data.DueDate is { Length: > 0 } ? data.DueDate.Trim() : null;

        // Gramática decimal do Pagaí como texto: sem símbolo, sem sinal e sem
        // arredondamento — valor fora da gramática vira revisão, nunca ajuste.
        if (amount is null)
        {
            return new InvoiceCandidate("needs_review", null, null, null, null, "missing_amount");
        }

        if (!PaguaiAmountGrammar.IsMatch(amount))
        {
            return new InvoiceCandidate("needs_review", null, null, null, null, "invalid_amount");
        }

        if (beneficiary is null)
        {
            return new InvoiceCandidate("needs_review", null, null, null, null, "missing_beneficiary");
        }

        if (dueDate is null || !DateOnly.TryParseExact(dueDate, "yyyy-MM-dd", out _))
        {
            return new InvoiceCandidate("needs_review", null, null, null, null, "missing_due_date");
        }

        var documentNumber = data.DocumentNumber is { Length: > 0 } ? data.DocumentNumber.Trim() : null;
        return new InvoiceCandidate("candidate", beneficiary, amount, dueDate, documentNumber, null);
    }

    private async Task RecordCandidateAsync(
        Guid inboundMessageId,
        InvoiceCandidate candidate,
        CancellationToken cancellationToken
    )
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT notifications.record_inbound_whatsapp_invoice_candidate("
            + "@inbound_id, @state, @beneficiary_name, @amount, @currency, @due_date, "
            + "@document_number, @needs_review_reason)";
        AddParameter(command, "inbound_id", inboundMessageId);
        AddParameter(command, "state", candidate.State);
        AddParameter(command, "beneficiary_name", candidate.BeneficiaryName is null ? DBNull.Value : candidate.BeneficiaryName);
        AddParameter(command, "amount", candidate.Amount is null ? DBNull.Value : candidate.Amount);
        AddParameter(command, "currency", candidate.Amount is null ? DBNull.Value : "BRL");
        AddParameter(
            command,
            "due_date",
            DateOnly.TryParseExact(candidate.DueDate, "yyyy-MM-dd", out var parsed)
                ? parsed
                : DBNull.Value
        );
        AddParameter(
            command,
            "document_number",
            candidate.DocumentNumber is null ? DBNull.Value : candidate.DocumentNumber
        );
        AddParameter(
            command,
            "needs_review_reason",
            candidate.NeedsReviewReason is null ? DBNull.Value : candidate.NeedsReviewReason
        );
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<bool> ActorIsAllowedAsync(Guid inboundMessageId, CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT notifications.inbound_document_actor_is_allowed(@inbound_id)";
        AddParameter(command, "inbound_id", inboundMessageId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<System.Data.Common.DbConnection> OpenConnectionAsync(
        CancellationToken cancellationToken
    )
    {
        var connection = database.Database.GetDbConnection();
        if (!connection.State.HasFlag(System.Data.ConnectionState.Open))
        {
            await connection.OpenAsync(cancellationToken);
        }

        return connection;
    }

    private static void AddParameter(System.Data.IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
