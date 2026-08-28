using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Configurations;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vinx.Erp.Paguai;

/// <summary>Persiste rascunhos Paguai e seus campos financeiros mínimos; não executa fluxo financeiro.</summary>
public sealed class PaguaiDraftService(
    FoundationDbContext database,
    CapabilityEvaluator capabilityEvaluator,
    AuditAppender auditAppender,
    DocumentUploadService documentUploads,
    FileStorage fileStorage,
    IDbContextOutbox outbox,
    ILogger<PaguaiDraftService> logger
)
{
    private const string CreateCommandName = "paguai.draft.create";
    private const string ProofFileUniqueIndexName = "uq_paguai_requests_proof_file";

    /// <summary>Vida útil máxima do link de leitura do comprovante; nunca persistido em payload.</summary>
    private static readonly TimeSpan ProofLinkLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Margem de segurança ao limitar o TTL pela retenção (latência de assinatura).</summary>
    private static readonly TimeSpan ProofLinkCapSafetyMargin = TimeSpan.FromSeconds(1);

    /// <summary>Classificação exigida do comprovante (fronteira pública de arquivos).</summary>
    public const string ProofClassification = "paguai_request_proof";

    private static readonly CapabilityRequirement ReadCapability = new(
        KnownCapabilities.PaguaiDraftRead,
        CapabilityScope.Company
    );
    private static readonly CapabilityRequirement CreateCapability = new(
        KnownCapabilities.PaguaiDraftCreate,
        CapabilityScope.Company
    );

    public async Task<PaguaiDraftResult> CreateAsync(
        ActorContext actor,
        CreateDraftCommand command,
        CancellationToken cancellationToken
    )
    {
        if (!PaguaiAmountRules.IsValid(command.Amount))
        {
            return PaguaiDraftResult.Of(PaguaiDraftState.Invalid);
        }

        command = command with { Amount = PaguaiAmountRules.Canonicalize(command.Amount) };
        var request = BuildDraftRequest(
            actor,
            command,
            Guid.NewGuid(),
            sourceProposalId: null);
        if (request is null)
        {
            return PaguaiDraftResult.Of(PaguaiDraftState.Invalid);
        }

        var hash = ComputePayloadHash(command);
        // One clock reading per operation: a new draft may only reference an
        // invoice still inside its retention window at this instant, while a
        // persisted replay stays valid after the window closes.
        var now = DateTimeOffset.UtcNow;
        var commitAttempted = false;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            var membership = await FindMembershipAsync(actor, command.CompanyId, cancellationToken);
            if (
                membership is null
                || !await capabilityEvaluator.HasGrantAsync(
                    command.CompanyId,
                    membership.Id,
                    CreateCapability,
                    cancellationToken
                )
            )
            {
                await transaction.RollbackAsync(cancellationToken);
                return await AuditedDenialAsync(actor, command.CompanyId, cancellationToken);
            }

            var existing = await FindIdempotencyAsync(actor, command, cancellationToken);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                if (!PayloadHashMatches(existing.PayloadHash, command, hash))
                {
                    return await AuditedConflictAsync(actor, command.CompanyId, cancellationToken);
                }

                return await ReplayWithFreshProofMetadataAsync(
                    actor,
                    command.CompanyId,
                    existing.ResultJson,
                    cancellationToken);
            }

            // O comprovante é validado e vinculado atomicamente com o rascunho:
            // fronteira Files (empresa/autoria/classificação/estado/retenção) sob
            // trava anti-TOCTOU. O replay acima já foi resolvido, então a
            // revalidação de retenção nunca bloqueia recibos persistidos.
            DocumentUploadService.DocumentBindingMetadata? bindMetadata = null;
            if (command.ProofFileId is { } proofFileId)
            {
                var validated = await ValidateBindableProofAsync(
                    actor,
                    command.CompanyId,
                    proofFileId,
                    lockRowForBinding: true,
                    cancellationToken
                );
                if (validated.Metadata is not { } validMetadata)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return await AuditedFailureAsync(
                        actor,
                        command.CompanyId,
                        ProofReason(validated.Rejection!.Value),
                        validated.Rejection.Value,
                        cancellationToken
                    );
                }

                bindMetadata = validMetadata;
            }

            if (command.InvoiceFileId is { } invoiceFileId)
            {
                var invoice = await database.Files.SingleOrDefaultAsync(
                    file => file.CompanyId == command.CompanyId && file.Id == invoiceFileId,
                    cancellationToken
                );
                if (
                    invoice is null
                    || invoice.RetentionUntil <= now
                    || !string.Equals(
                        invoice.ProcessingState,
                        FileRecord.ConfirmedState,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(
                        invoice.Classification,
                        "paguai_request_invoice",
                        StringComparison.Ordinal
                    )
                )
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return await AuditedFailureAsync(
                        actor,
                        command.CompanyId,
                        "invoice_file_invalid",
                        PaguaiDraftState.Invalid,
                        cancellationToken
                    );
                }
            }

            database.PaguaiRequests.Add(request);
            await database.SaveChangesAsync(cancellationToken);
            var draft = ToDraft(
                request,
                bindMetadata?.FileName,
                bindMetadata?.MimeType,
                bindMetadata?.SizeBytes);
            // O recibo persiste APENAS o ponteiro: nenhum metadado de arquivo é
            // duplicado fora de foundation.files; o replay reenriquece com
            // verificação fresca de ciclo de vida na hora da leitura.
            database.IdempotencyRecords.Add(
                new IdempotencyRecord(
                    Guid.NewGuid(),
                    command.CompanyId,
                    actor.UserId,
                    CreateCommandName,
                    command.RequestId,
                    hash,
                    JsonSerializer.Serialize(ToDraft(request))
                )
            );
            await auditAppender.AppendAsync(
                new AuditEventData(
                    actor.UserId,
                    AuditScope.Company,
                    command.CompanyId,
                    AuditSource.Web,
                    CreateCommandName,
                    "paguai_request",
                    draft.Id,
                    AuditOutcome.Success,
                    actor.CorrelationId,
                    command.ProofFileId is null ? null : "proof_attached",
                    "{}"
                ),
                cancellationToken
            );
            await database.SaveChangesAsync(cancellationToken);
            outbox.Enroll(database);
            await outbox.PublishAsync(
                new PaguaiDraftCreated(command.CompanyId, draft.Id, actor.UserId),
                new DeliveryOptions { CorrelationId = actor.CorrelationId.ToString("D") }
            );
            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);

            try
            {
                await outbox.FlushOutgoingMessagesAsync();
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Paguai draft {Operation} {Module} {Result} {Reason}",
                    "create",
                    "paguai",
                    "created",
                    "outbox_flush_deferred"
                );
            }

            return new PaguaiDraftResult(PaguaiDraftState.Created, draft);
        }
        catch (DbUpdateException exception)
            when (IsProofBindUniqueViolation(exception))
        {
            // Defesa dupla contra corrida de vínculo na criação: o índice
            // parcial único rejeita no storage, mas antes de classificar como
            // duplo vínculo o recibo de idempotência é reconciliado — duas
            // criações idênticas concorrentes (mesma chave, mesmo comprovante)
            // resultam em 201 + replay 200, nunca em 409 espúrio.
            database.ChangeTracker.Clear();
            return await ReconcileProofIndexConflictAsync(actor, command, hash, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            database.ChangeTracker.Clear();
            return IsSourceDocumentConflict(exception)
                ? await ReconcileSourceDocumentConflictAsync(actor, command, cancellationToken)
                : await ReconcileReplayAsync(actor, command, hash, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            logger.LogError(
                exception,
                "Paguai draft {Operation} {Module} {Result} {Reason}",
                "create",
                "paguai",
                "unavailable",
                commitAttempted ? "outbox_commit_outcome_unknown" : "database_or_audit_unavailable"
            );
            return PaguaiDraftResult.Of(PaguaiDraftState.Unavailable);
        }
    }

    /// <summary>
    /// Rascunho idempotente a partir de um candidato admitido do WhatsApp
    /// (#272 PR4): valor/vencimento/beneficiário vêm do candidato tipado
    /// (revalidados, nunca confiados), title/description/proof são sempre
    /// explícitos e o rascunho fica único por documento. A criação automática
    /// permanece decisão pendente do #272 — este caminho é sempre requerido
    /// por um ator autenticado com capability de criar rascunho.
    /// </summary>
    public async Task<PaguaiDraftResult> CreateFromInboundDocumentAsync(
        ActorContext actor,
        CreateDraftFromDocumentCommand command,
        CancellationToken cancellationToken
    )
    {
        if (
            command.CompanyId == Guid.Empty
            || command.InboundMessageId == Guid.Empty
            || string.IsNullOrWhiteSpace(command.Title)
            || string.IsNullOrWhiteSpace(command.Description)
            || (
                command.ProofRequirement != "optional"
                && command.ProofRequirement != "required"
            )
        )
        {
            return PaguaiDraftResult.Of(PaguaiDraftState.Invalid);
        }

        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var readCommand = connection.CreateCommand();
            readCommand.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
            readCommand.CommandText =
                "SELECT company_id, state, beneficiary_name, amount, currency, due_date "
                + "FROM notifications.read_inbound_whatsapp_invoice_candidate(@inbound_id)";
            var inboundId = readCommand.CreateParameter();
            inboundId.ParameterName = "inbound_id";
            inboundId.Value = command.InboundMessageId;
            readCommand.Parameters.Add(inboundId);
            bool found = false;
            var candidateCompanyId = Guid.Empty;
            string candidateState = string.Empty;
            string beneficiary = string.Empty;
            string amountText = string.Empty;
            string currency = string.Empty;
            var dueDate = default(DateOnly);
            await using (var reader = await readCommand.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    found = true;
                    candidateCompanyId = reader.GetGuid(0);
                    candidateState = reader.GetString(1);
                    if (candidateState == "candidate")
                    {
                        beneficiary = reader.GetString(2);
                        amountText = reader.GetString(3);
                        currency = reader.GetString(4);
                        dueDate = reader.GetFieldValue<DateOnly>(5);
                    }
                }
            }

            if (
                !found
                || candidateCompanyId != command.CompanyId
                || candidateState != "candidate"
                || beneficiary.Length is < 1 or > 200
                || !PaguaiAmountRules.TryParseText(amountText, out var amount)
            )
            {
                // Sem candidato visível/fechado, candidato de outra empresa
                // (mesmo que o ator tenha membership lá) ou gramática
                // reprovada na fronteira: sem confiança silenciosa em dado
                // persistido e sem copiar dados entre empresas.
                await transaction.RollbackAsync(cancellationToken);
                return PaguaiDraftResult.Of(PaguaiDraftState.Invalid);
            }

            await transaction.CommitAsync(cancellationToken);
            return await CreateAsync(
                actor,
                new CreateDraftCommand(
                    CompanyId: command.CompanyId,
                    RequestId: command.InboundMessageId,
                    Title: command.Title,
                    Description: command.Description,
                    Amount: amount,
                    Currency: currency,
                    DueDate: dueDate,
                    BeneficiaryDisplayName: beneficiary,
                    ProofRequirement: command.ProofRequirement,
                    SourceInboundMessageId: command.InboundMessageId,
                    InvoiceFileId: null
                ),
                cancellationToken
            );
        }
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

    public async Task<PaguaiDraftListResult> ListAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    )
    {
        if (companyId == Guid.Empty)
        {
            return PaguaiDraftListResult.Of(PaguaiDraftState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await ApplyContextAsync(actor, companyId, cancellationToken);
            var membership = await FindMembershipAsync(actor, companyId, cancellationToken);
            if (
                membership is null
                || !await capabilityEvaluator.HasGrantAsync(
                    companyId,
                    membership.Id,
                    ReadCapability,
                    cancellationToken
                )
            )
            {
                await transaction.RollbackAsync(cancellationToken);
                return PaguaiDraftListResult.Of(PaguaiDraftState.Forbidden);
            }

            var drafts = await ReadListAsync(companyId, actor.UserId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PaguaiDraftListResult(PaguaiDraftState.Created, drafts);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Paguai draft {Operation} {Module} {Result} {Reason}",
                "list",
                "paguai",
                "unavailable",
                "database_unavailable"
            );
            return PaguaiDraftListResult.Of(PaguaiDraftState.Unavailable);
        }
    }

    public async Task<PaguaiDraftResult> GetAsync(
        ActorContext actor,
        Guid companyId,
        Guid id,
        CancellationToken cancellationToken
    )
    {
        if (companyId == Guid.Empty || id == Guid.Empty)
        {
            return PaguaiDraftResult.Of(PaguaiDraftState.NotFound);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await ApplyContextAsync(actor, companyId, cancellationToken);
            var membership = await FindMembershipAsync(actor, companyId, cancellationToken);
            if (
                membership is null
                || !await capabilityEvaluator.HasGrantAsync(
                    companyId,
                    membership.Id,
                    ReadCapability,
                    cancellationToken
                )
            )
            {
                await transaction.RollbackAsync(cancellationToken);
                return PaguaiDraftResult.Of(PaguaiDraftState.NotFound);
            }

            var request = await FindRequestAsync(companyId, actor.UserId, id, cancellationToken);
            // Detalhe expõe apenas metadados seguros do comprovante (nome, MIME,
            // tamanho) quando o arquivo segue válido; nunca chave do provedor,
            // checksum, retenção ou URL. A lista carrega só o ponteiro.
            PaguaiDraft? draft = null;
            if (request?.ProofFileId is { } proofFileId)
            {
                var detail = await documentUploads.ValidateActiveForBindAsync(
                    actor,
                    companyId,
                    proofFileId,
                    requiredClassification: null,
                    lockRowForBinding: false,
                    cancellationToken
                );
                draft = ToDraft(request, detail.Value?.FileName, detail.Value?.MimeType, detail.Value?.SizeBytes);
            }
            else if (request is not null)
            {
                draft = ToDraft(request);
            }

            await transaction.CommitAsync(cancellationToken);
            return request is null
                ? PaguaiDraftResult.Of(PaguaiDraftState.NotFound)
                : new PaguaiDraftResult(PaguaiDraftState.Created, draft);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Paguai draft {Operation} {Module} {Result} {Reason}",
                "detail",
                "paguai",
                "unavailable",
                "database_unavailable"
            );
            return PaguaiDraftResult.Of(PaguaiDraftState.Unavailable);
        }
    }

    /// <summary>
    /// Emite URL assinada de curta duração para o comprovante vinculado sob
    /// capability e RLS frescos, revalidando classificação, ciclo de vida e
    /// retenção imediatamente antes da assinatura. O TTL nunca excede cinco
    /// minutos nem sobrevive à retenção do arquivo; a URL nunca entra em
    /// payloads de lista/detalhe.
    /// </summary>
    public async Task<PaguaiProofLinkResult> OpenProofLinkAsync(
        ActorContext actor,
        Guid companyId,
        Guid draftId,
        CancellationToken cancellationToken
    )
    {
        if (companyId == Guid.Empty || draftId == Guid.Empty)
        {
            return PaguaiProofLinkResult.Of(PaguaiDraftState.NotFound);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await ApplyContextAsync(actor, companyId, cancellationToken);
            var membership = await FindMembershipAsync(actor, companyId, cancellationToken);
            if (
                membership is null
                || !await capabilityEvaluator.HasGrantAsync(
                    companyId,
                    membership.Id,
                    ReadCapability,
                    cancellationToken
                )
            )
            {
                await transaction.CommitAsync(cancellationToken);
                return PaguaiProofLinkResult.Of(PaguaiDraftState.NotFound);
            }

            var request = await FindRequestAsync(companyId, actor.UserId, draftId, cancellationToken);
            if (request?.ProofFileId is not { } proofFileId)
            {
                await transaction.CommitAsync(cancellationToken);
                return PaguaiProofLinkResult.Of(PaguaiDraftState.NotFound);
            }

            // Autorização fresca pela fronteira Files antes de qualquer assinatura:
            // classificação, estado, exclusão e retenção valem AGORA, não no bind.
            var linkage = await documentUploads.ValidateActiveForBindAsync(
                actor,
                companyId,
                proofFileId,
                requiredClassification: ProofClassification,
                lockRowForBinding: false,
                cancellationToken
            );
            if (linkage.Value is not { } metadata)
            {
                await transaction.CommitAsync(cancellationToken);
                return linkage.ErrorCode == "file_not_ready"
                    ? PaguaiProofLinkResult.Of(PaguaiDraftState.ProofFileNotReady)
                    : PaguaiProofLinkResult.Of(PaguaiDraftState.NotFound);
            }

            // TTL = min(5 minutos, retenção restante no relógio autoritativo),
            // com margem conservadora quando o teto é a retenção: a URL não pode
            // sobreviver à retenção nem pela latência entre esta leitura e a
            // assinatura do provedor. O provedor trunca para segundos inteiros,
            // então exigimos margem + 1 segundo válido: menos que isso falha
            // fechado (404), nunca um expiresIn 0/subsegundo nem um 503.
            var serverNow = await documentUploads.ReadServerClockAsync(cancellationToken);
            var remaining = metadata.RetentionUntil.UtcDateTime - serverNow;
            if (remaining < ProofLinkCapSafetyMargin + TimeSpan.FromSeconds(1))
            {
                await transaction.CommitAsync(cancellationToken);
                return PaguaiProofLinkResult.Of(PaguaiDraftState.NotFound);
            }

            var lifetime = ProofLinkLifetime;
            if (remaining < ProofLinkLifetime)
            {
                lifetime = remaining - ProofLinkCapSafetyMargin;
            }
            var signed = await fileStorage.CreateSignedDownloadUrlAsync(
                metadata.ProviderKey,
                lifetime,
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            // A expiração exposta nunca ultrapassa a retenção do arquivo.
            var expiresAt = signed.ExpiresAt < metadata.RetentionUntil
                ? signed.ExpiresAt
                : metadata.RetentionUntil;
            return new PaguaiProofLinkResult(
                PaguaiDraftState.Created,
                new PaguaiProofLink(signed.Url, expiresAt)
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Paguai proof {Operation} {Module} {Result} {Reason}",
                "open_link",
                "paguai",
                "unavailable",
                "database_or_storage_unavailable"
            );
            return PaguaiProofLinkResult.Of(PaguaiDraftState.Unavailable);
        }
    }

    private sealed record ProofBindingValidation(
        PaguaiDraftState? Rejection,
        DocumentUploadService.DocumentBindingMetadata? Metadata);

    /// <summary>
    /// Vínculo validado pela fronteira pública Foundation (existência, empresa,
    /// autoria do ator, classificação <c>paguai_request_proof</c>, estado
    /// confirmado pós-quarentena/scan e retenção viva, com trava quando pedido) mais
    /// a unicidade lógica de um comprovante por rascunho.
    /// </summary>
    private async Task<ProofBindingValidation> ValidateBindableProofAsync(
        ActorContext actor,
        Guid companyId,
        Guid proofFileId,
        bool lockRowForBinding = true,
        CancellationToken cancellationToken = default
    )
    {
        var validated = await documentUploads.ValidateActiveForBindAsync(
            actor,
            companyId,
            proofFileId,
            requiredClassification: ProofClassification,
            lockRowForBinding,
            cancellationToken
        );
        if (validated.Value is { } metadata)
        {
            var boundElsewhere = await database.PaguaiRequests.AsNoTracking().AnyAsync(
                request => request.ProofFileId == proofFileId,
                cancellationToken
            );
            return new ProofBindingValidation(
                boundElsewhere ? PaguaiDraftState.ProofFileBound : null,
                metadata
            );
        }

        return new ProofBindingValidation(
            validated.ErrorCode switch
            {
                "file_not_ready" => PaguaiDraftState.ProofFileNotReady,
                _ => PaguaiDraftState.ProofFileNotFound,
            },
            null
        );
    }

    /// <summary>Razões controladas de auditoria de comprovante; nunca texto livre do usuário.</summary>
    private static string ProofReason(PaguaiDraftState state) =>
        state switch
        {
            PaguaiDraftState.ProofFileNotFound => "proof_file_not_found",
            PaguaiDraftState.ProofFileNotReady => "proof_file_not_ready",
            PaguaiDraftState.ProofFileBound => "proof_file_bound",
            _ => "invalid_request",
        };

    private async Task<PaguaiDraftResult> ReconcileProofIndexConflictAsync(
        ActorContext actor,
        CreateDraftCommand command,
        string hash,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            var membership = await FindMembershipAsync(actor, command.CompanyId, cancellationToken);
            if (
                membership is null
                || !await capabilityEvaluator.HasGrantAsync(
                    command.CompanyId,
                    membership.Id,
                    CreateCapability,
                    cancellationToken
                )
            )
            {
                await transaction.RollbackAsync(cancellationToken);
                return await AuditedDenialAsync(actor, command.CompanyId, cancellationToken);
            }

            // O 23505 do índice parcial único pode ser a perda de uma corrida
            // entre criações IDÊNTICAS: o vencedor já commitou o recibo, então
            // o perdedor responde replay 200. Sem recibo (e sem hash igual) é
            // um duplo vínculo real de outro rascunho — 409 tipado.
            var existing = await FindIdempotencyAsync(actor, command, cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            if (existing is null)
            {
                return await AuditedFailureAsync(
                    actor,
                    command.CompanyId,
                    ProofReason(PaguaiDraftState.ProofFileBound),
                    PaguaiDraftState.ProofFileBound,
                    cancellationToken
                );
            }

            return PayloadHashMatches(existing.PayloadHash, command, hash)
                ? await ReplayWithFreshProofMetadataAsync(
                    actor,
                    command.CompanyId,
                    existing.ResultJson,
                    cancellationToken)
                : await AuditedConflictAsync(actor, command.CompanyId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Paguai draft {Operation} {Module} {Result} {Reason}",
                "reconcile",
                "paguai",
                "unavailable",
                "proof_index_reconciliation_failed"
            );
            return PaguaiDraftResult.Of(PaguaiDraftState.Unavailable);
        }
    }

    private async Task<PaguaiDraftResult> ReconcileReplayAsync(
        ActorContext actor,
        CreateDraftCommand command,
        string hash,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            var membership = await FindMembershipAsync(actor, command.CompanyId, cancellationToken);
            if (
                membership is null
                || !await capabilityEvaluator.HasGrantAsync(
                    command.CompanyId,
                    membership.Id,
                    CreateCapability,
                    cancellationToken
                )
            )
            {
                await transaction.RollbackAsync(cancellationToken);
                return await AuditedDenialAsync(actor, command.CompanyId, cancellationToken);
            }

            var existing = await FindIdempotencyAsync(actor, command, cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            if (existing is null)
            {
                return PaguaiDraftResult.Of(PaguaiDraftState.Unavailable);
            }

            return PayloadHashMatches(existing.PayloadHash, command, hash)
                ? await ReplayWithFreshProofMetadataAsync(
                    actor,
                    command.CompanyId,
                    existing.ResultJson,
                    cancellationToken)
                : await AuditedConflictAsync(actor, command.CompanyId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Paguai draft {Operation} {Module} {Result} {Reason}",
                "reconcile",
                "paguai",
                "unavailable",
                "idempotency_reconciliation_failed"
            );
            return PaguaiDraftResult.Of(PaguaiDraftState.Unavailable);
        }
    }

    /// <summary>
    /// Concilia a corrida no documento inbound: o documento já possui um
    /// rascunho (único por constraint global). Reautoriza, registra a
    /// negação auditada e responde com o conflito 409 sem expor dados do
    /// rascunho ocupante — no máximo o seu id, e somente quando visível
    /// ao próprio ator sob RLS.
    /// </summary>
    private async Task<PaguaiDraftResult> ReconcileSourceDocumentConflictAsync(
        ActorContext actor,
        CreateDraftCommand command,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            var membership = await FindMembershipAsync(actor, command.CompanyId, cancellationToken);
            if (
                membership is null
                || !await capabilityEvaluator.HasGrantAsync(
                    command.CompanyId,
                    membership.Id,
                    CreateCapability,
                    cancellationToken
                )
            )
            {
                await transaction.RollbackAsync(cancellationToken);
                return await AuditedDenialAsync(actor, command.CompanyId, cancellationToken);
            }

            var existing = command.SourceInboundMessageId is { } sourceInboundMessageId
                ? await FindRequestBySourceDocumentAsync(
                    command.CompanyId,
                    sourceInboundMessageId,
                    cancellationToken
                )
                : null;
            await transaction.RollbackAsync(cancellationToken);
            return await AuditedFailureAsync(
                actor,
                command.CompanyId,
                "source_document_conflict",
                PaguaiDraftState.IdempotencyConflict,
                cancellationToken,
                existing?.Id
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Paguai draft {Operation} {Module} {Result} {Reason}",
                "reconcile",
                "paguai",
                "unavailable",
                "source_document_reconciliation_failed"
            );
            return PaguaiDraftResult.Of(PaguaiDraftState.Unavailable);
        }
    }

    private async Task<PaguaiDraftResult> AuditedDenialAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    ) =>
        await AuditedFailureAsync(
            actor,
            companyId,
            "capability_denied",
            PaguaiDraftState.Forbidden,
            cancellationToken
        );

    private async Task<PaguaiDraftResult> AuditedConflictAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    ) =>
        await AuditedFailureAsync(
            actor,
            companyId,
            "idempotency_conflict",
            PaguaiDraftState.IdempotencyConflict,
            cancellationToken
        );

    private async Task<PaguaiDraftResult> AuditedFailureAsync(
        ActorContext actor,
        Guid companyId,
        string reason,
        PaguaiDraftState state,
        CancellationToken cancellationToken,
        Guid? resourceId = null
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await ApplyContextAsync(actor, companyId, cancellationToken);
            await auditAppender.AppendAsync(
                new AuditEventData(
                    actor.UserId,
                    AuditScope.Company,
                    companyId,
                    AuditSource.Web,
                    reason,
                    "paguai_request",
                    resourceId,
                    AuditOutcome.Denied,
                    actor.CorrelationId,
                    reason,
                    "{}"
                ),
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            return PaguaiDraftResult.Of(state);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Paguai draft {Operation} {Module} {Result} {Reason}",
                "audit_denial",
                "paguai",
                "unavailable",
                "audit_unavailable"
            );
            return PaguaiDraftResult.Of(PaguaiDraftState.Unavailable);
        }
    }

    private Task<Membership?> FindMembershipAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    ) =>
        database.Memberships.SingleOrDefaultAsync(
            membership => membership.CompanyId == companyId && membership.UserId == actor.UserId,
            cancellationToken
        );

    private Task<IdempotencyRecord?> FindIdempotencyAsync(
        ActorContext actor,
        CreateDraftCommand command,
        CancellationToken cancellationToken
    ) =>
        database.IdempotencyRecords.SingleOrDefaultAsync(
            record =>
                record.CompanyId == command.CompanyId
                && record.ActorUserId == actor.UserId
                && record.CommandName == CreateCommandName
                && record.RequestId == command.RequestId,
            cancellationToken
        );

    private Task<PaguaiRequest?> FindRequestAsync(
        Guid companyId,
        Guid userId,
        Guid id,
        CancellationToken cancellationToken
    ) =>
        database.PaguaiRequests.AsNoTracking().SingleOrDefaultAsync(
            request =>
                request.CompanyId == companyId
                && request.RequesterUserId == userId
                && request.Id == id,
            cancellationToken
        );

    private Task<PaguaiRequest?> FindRequestBySourceDocumentAsync(
        Guid companyId,
        Guid sourceInboundMessageId,
        CancellationToken cancellationToken
    ) =>
        database.PaguaiRequests.AsNoTracking().SingleOrDefaultAsync(
            request =>
                request.CompanyId == companyId
                && request.SourceInboundMessageId == sourceInboundMessageId,
            cancellationToken
        );

    private async Task<IReadOnlyList<PaguaiDraft>> ReadListAsync(
        Guid companyId,
        Guid userId,
        CancellationToken cancellationToken
    )
    {
        var requests = await database
            .PaguaiRequests.AsNoTracking()
            .Where(request =>
                request.CompanyId == companyId && request.RequesterUserId == userId
            )
            .OrderByDescending(request => request.CreatedAt)
            .ThenByDescending(request => request.Id)
            .ToListAsync(cancellationToken);
        // Lista carrega apenas o ponteiro do comprovante; metadados vêm da
        // fronteira apenas no detalhe.
        return [.. requests.Select(request => ToDraft(request))];
    }

    /// <summary>Valida e constrói um rascunho sem persistência nem transação.</summary>
    internal static PaguaiRequest? BuildDraftRequest(
        ActorContext actor,
        CreateDraftCommand command,
        Guid id,
        Guid? sourceProposalId)
    {
        if (id == Guid.Empty
            || actor.UserId == Guid.Empty
            || command.CompanyId == Guid.Empty
            || command.RequestId == Guid.Empty
            || command.InvoiceFileId == Guid.Empty
            || command.SourceInboundMessageId == Guid.Empty
            || sourceProposalId == Guid.Empty
            || (sourceProposalId is not null && command.SourceInboundMessageId is not null)
            || !IsCanonicalText(command.Title, 200)
            || !IsCanonicalText(command.Description, 4_000)
            || !IsCanonicalText(command.BeneficiaryDisplayName, 200)
            || !PaguaiAmountRules.IsValid(command.Amount)
            || command.Currency != "BRL"
            || command.DueDate == default
            || command.ProofRequirement is not ("optional" or "required"))
        {
            return null;
        }

        return new PaguaiRequest
        {
            Id = id,
            CompanyId = command.CompanyId,
            RequesterUserId = actor.UserId,
            RequestId = command.RequestId,
            Title = command.Title,
            Description = command.Description,
            Amount = PaguaiAmountRules.Canonicalize(command.Amount),
            Currency = command.Currency,
            DueDate = command.DueDate,
            BeneficiaryDisplayName = command.BeneficiaryDisplayName,
            ProofRequirement = command.ProofRequirement,
            ProofFileId = command.ProofFileId,
            InvoiceFileId = command.InvoiceFileId,
            SourceInboundMessageId = command.SourceInboundMessageId,
            SourceProposalId = sourceProposalId,
            State = "rascunho",
            Version = 1,
        };
    }

    /// <summary>
    /// Projeta o rascunho com metadados seguros opcionais do comprovante
    /// (nome, MIME e tamanho); nunca chave do provedor, checksum ou URL.
    /// </summary>
    internal static PaguaiDraft ToDraft(
        PaguaiRequest request,
        string? proofFileName = null,
        string? proofFileMimeType = null,
        long? proofFileSizeBytes = null
    ) =>
        new(
            request.Id,
            request.CompanyId,
            request.RequestId,
            request.Title,
            request.Description,
            request.Amount,
            request.Currency,
            request.DueDate,
            request.BeneficiaryDisplayName,
            request.ProofRequirement,
            request.InvoiceFileId,
            request.State,
            request.Version,
            request.CreatedAt,
            request.ProofFileId,
            proofFileName,
            proofFileMimeType,
            proofFileSizeBytes
        );

    private static bool IsCanonicalText(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value == value.Trim()
        && !value.Any(char.IsControl);

    /// <summary>
    /// Replay com enriquecimento fresco: o recibo persiste apenas o ponteiro
    /// do comprovante, então nome/MIME/tamanho vêm da fronteira Files AGORA,
    /// sob verificação de ciclo de vida — arquivo vencido/removido devolve o
    /// rascunho original com ponteiro e SEM metadados stale.
    /// </summary>
    private async Task<PaguaiDraftResult> ReplayWithFreshProofMetadataAsync(
        ActorContext actor,
        Guid companyId,
        string resultJson,
        CancellationToken cancellationToken
    )
    {
        var replay = DeserializeReplay(resultJson);
        if (
            replay.State != PaguaiDraftState.Replay
            || replay.Draft?.ProofFileId is not { } proofFileId
        )
        {
            return replay;
        }

        var detail = await documentUploads.ValidateActiveForBindAsync(
            actor,
            companyId,
            proofFileId,
            requiredClassification: ProofClassification,
            lockRowForBinding: false,
            cancellationToken
        );
        if (detail.Value is not { } metadata)
        {
            return replay;
        }

        return new PaguaiDraftResult(
            PaguaiDraftState.Replay,
            replay.Draft with
            {
                ProofFileName = metadata.FileName,
                ProofFileMimeType = metadata.MimeType,
                ProofFileSizeBytes = metadata.SizeBytes,
            }
        );
    }

    private static PaguaiDraftResult DeserializeReplay(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (
                root.TryGetProperty(nameof(PaguaiDraft.Amount), out _)
                && JsonSerializer.Deserialize<PaguaiDraft>(resultJson) is { } draft
            )
            {
                return new PaguaiDraftResult(PaguaiDraftState.Replay, draft);
            }

            if (
                root.TryGetProperty(nameof(LegacyPaguaiDraft.AmountMinor), out _)
                && JsonSerializer.Deserialize<LegacyPaguaiDraft>(resultJson) is { } legacy
                && legacy.AmountMinor > 0
            )
            {
                return new PaguaiDraftResult(
                    PaguaiDraftState.Replay,
                    new PaguaiDraft(
                        legacy.Id,
                        legacy.CompanyId,
                        legacy.RequestId,
                        legacy.Title,
                        legacy.Description,
                        legacy.AmountMinor / 100m,
                        legacy.Currency,
                        legacy.DueDate,
                        legacy.BeneficiaryDisplayName,
                        legacy.ProofRequirement,
                        null,
                        legacy.State,
                        legacy.Version,
                        legacy.CreatedAt
                    )
                );
            }
        }
        catch (JsonException)
        {
            // Corrupt receipts are reported as unavailable by the caller.
        }

        return PaguaiDraftResult.Of(PaguaiDraftState.Unavailable);
    }

    private sealed record LegacyPaguaiDraft(
        Guid Id,
        Guid CompanyId,
        Guid RequestId,
        string Title,
        string Description,
        long AmountMinor,
        string Currency,
        DateOnly DueDate,
        string BeneficiaryDisplayName,
        string ProofRequirement,
        string State,
        int Version,
        DateTimeOffset CreatedAt
    );

    private const string SourceInboundMessageConstraintName =
        "uq_paguai_requests_source_inbound_message";

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        IsUniqueViolation(exception, IdempotencyRecordConfiguration.KeyIndexDatabaseName)
        || IsUniqueViolation(exception, SourceInboundMessageConstraintName);

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName)
    {
        if (exception.InnerException is not PostgresException postgresException)
        {
            return false;
        }

        return postgresException.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(
                postgresException.ConstraintName,
                constraintName,
                StringComparison.Ordinal
            );
    }

    /// <summary>
    /// Classifica o 23505 do índice parcial único do comprovante a partir de
    /// qualquer forma de exceção (DbUpdateException do bind direto ou
    /// NpgsqlException crua vinda do ExecuteUpdate na troca).
    /// </summary>
    private static bool IsProofBindUniqueViolation(Exception exception)
    {
        for (
            Exception? current = exception;
            current is not null;
            current = current.InnerException
        )
        {
            if (
                current is PostgresException postgres
                && postgres.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(
                    postgres.ConstraintName,
                    ProofFileUniqueIndexName,
                    StringComparison.Ordinal
                )
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSourceDocumentConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgresException
        && string.Equals(
            postgresException.ConstraintName,
            SourceInboundMessageConstraintName,
            StringComparison.Ordinal
        );

    private static string ComputePayloadHash(CreateDraftCommand command)
    {
        // Sem comprovante o canônico fica byte-idêntico ao corte anterior,
        // preservando replay de recibos existentes; com comprovante o hash
        // passa a cobri-lo, então a mesma chave idempotente com outro
        // arquivo vira conflito tipado.
        object canonical = command.ProofFileId is { } proofFileId
            ? new
            {
                companyId = command.CompanyId,
                requestId = command.RequestId,
                title = command.Title,
                description = command.Description,
                amount = command.Amount,
                currency = command.Currency,
                dueDate = command.DueDate,
                beneficiaryDisplayName = command.BeneficiaryDisplayName,
                proofRequirement = command.ProofRequirement,
                invoiceFileId = command.InvoiceFileId,
                proofFileId,
            }
            : new
            {
                companyId = command.CompanyId,
                requestId = command.RequestId,
                title = command.Title,
                description = command.Description,
                amount = command.Amount,
                currency = command.Currency,
                dueDate = command.DueDate,
                beneficiaryDisplayName = command.BeneficiaryDisplayName,
                proofRequirement = command.ProofRequirement,
                invoiceFileId = command.InvoiceFileId,
            };
        return HashCanonicalPayload(JsonSerializer.Serialize(canonical));
    }

    private static string ComputeLegacyDecimalPayloadHash(CreateDraftCommand command)
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                companyId = command.CompanyId,
                requestId = command.RequestId,
                title = command.Title,
                description = command.Description,
                amount = command.Amount,
                currency = command.Currency,
                dueDate = command.DueDate,
                beneficiaryDisplayName = command.BeneficiaryDisplayName,
                proofRequirement = command.ProofRequirement,
            }
        );
        return HashCanonicalPayload(canonical);
    }

    private static string ComputeLegacyAmountMinorPayloadHash(CreateDraftCommand command)
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                companyId = command.CompanyId,
                requestId = command.RequestId,
                title = command.Title,
                description = command.Description,
                amountMinor = decimal.ToInt64(command.Amount * 100m),
                currency = command.Currency,
                dueDate = command.DueDate,
                beneficiaryDisplayName = command.BeneficiaryDisplayName,
                proofRequirement = command.ProofRequirement,
            }
        );
        return HashCanonicalPayload(canonical);
    }

    private static bool PayloadHashMatches(
        string existingHash,
        CreateDraftCommand command,
        string currentHash
    ) => string.Equals(existingHash, currentHash, StringComparison.Ordinal)
        || (
            // Hashes legados só casam com payloads sem fatura E sem
            // comprovante: um recibo pré-corte jamais pode reabrir replay de
            // uma criação que agora traz comprovante (409 idempotency_conflict).
            command.InvoiceFileId is null
            && command.ProofFileId is null
            && (
                string.Equals(
                    existingHash,
                    ComputeLegacyDecimalPayloadHash(command),
                    StringComparison.Ordinal
                )
                || string.Equals(
                    existingHash,
                    ComputeLegacyAmountMinorPayloadHash(command),
                    StringComparison.Ordinal
                )
            )
        );

    private static string HashCanonicalPayload(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    private Task ApplyContextAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    ) => FoundationRlsContext.ApplyCommandAsync(
        database.Database,
        actor,
        companyId,
        cancellationToken);
}
