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
    IDbContextOutbox outbox,
    ILogger<PaguaiDraftService> logger
)
{
    private const string CreateCommandName = "paguai.draft.create";
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

                return DeserializeReplay(existing.ResultJson);
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
            var draft = ToDraft(request);
            database.IdempotencyRecords.Add(
                new IdempotencyRecord(
                    Guid.NewGuid(),
                    command.CompanyId,
                    actor.UserId,
                    CreateCommandName,
                    command.RequestId,
                    hash,
                    JsonSerializer.Serialize(draft)
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
                    null,
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
            await transaction.CommitAsync(cancellationToken);
            return request is null
                ? PaguaiDraftResult.Of(PaguaiDraftState.NotFound)
                : new PaguaiDraftResult(PaguaiDraftState.Created, ToDraft(request));
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
                ? DeserializeReplay(existing.ResultJson)
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
        return [.. requests.Select(ToDraft)];
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
            InvoiceFileId = command.InvoiceFileId,
            SourceInboundMessageId = command.SourceInboundMessageId,
            SourceProposalId = sourceProposalId,
            State = "rascunho",
            Version = 1,
        };
    }

    internal static PaguaiDraft ToDraft(PaguaiRequest request) =>
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
            request.CreatedAt
        );

    private static bool IsCanonicalText(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value == value.Trim()
        && !value.Any(char.IsControl);

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

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        if (exception.InnerException is not PostgresException postgresException)
        {
            return false;
        }

        return postgresException.SqlState == PostgresErrorCodes.UniqueViolation
            && (
                string.Equals(
                    postgresException.ConstraintName,
                    IdempotencyRecordConfiguration.KeyIndexDatabaseName,
                    StringComparison.Ordinal
                )
                || string.Equals(
                    postgresException.ConstraintName,
                    SourceInboundMessageConstraintName,
                    StringComparison.Ordinal
                )
            );
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
                invoiceFileId = command.InvoiceFileId,
            }
        );
        return HashCanonicalPayload(canonical);
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
            command.InvoiceFileId is null
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
