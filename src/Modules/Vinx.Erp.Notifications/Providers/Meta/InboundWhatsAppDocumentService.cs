using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Notifications.Providers.Meta;

/// <summary>
/// Fatia de documento seguro (#272 PR2): baixa o documento admitido da Meta,
/// valida MIME/bytes/páginas, faz o scan antimalware e armazena em quarentena
/// privada antes de registrar a admissão durável — sem OCR, sem Pagaí e sem
/// resposta automática. Falha fechada com motivos estáveis.
/// </summary>
public sealed class InboundWhatsAppDocumentService(
    FoundationDbContext database,
    IHttpClientFactory httpClientFactory,
    MetaWhatsAppOptions metaOptions,
    FileStorage storage,
    IMalwareScanner malwareScanner,
    IOptions<MalwareScannerOptions> malwareScannerOptions,
    ILogger<InboundWhatsAppDocumentService> logger
)
{
    private const string Operation = "whatsapp_inbound_document";
    private const string Module = "notifications";

    private static readonly DocumentContextLimits PdfLimits = new();
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

    private sealed record DocumentClaim(string MediaId, Guid ResolvedCompanyId);

    private async Task<DocumentClaim?> ClaimAsync(Guid inboundMessageId, CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT status, claimed, media_id, resolved_company_id FROM "
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

            return new DocumentClaim(reader.GetString(2), reader.GetGuid(3));
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
