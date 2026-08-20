using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Configurations;

namespace Vinx.Erp.Paguai;

/// <summary>Persiste somente metadados de rascunho Paguai; não executa fluxo financeiro.</summary>
public sealed class PaguaiDraftService(
    FoundationDbContext database,
    CapabilityEvaluator capabilityEvaluator,
    AuditAppender auditAppender,
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
        var hash = ComputePayloadHash(command);
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
                if (!string.Equals(existing.PayloadHash, hash, StringComparison.Ordinal))
                {
                    return await AuditedConflictAsync(actor, command.CompanyId, cancellationToken);
                }

                return DeserializeReplay(existing.ResultJson);
            }

            var request = new PaguaiRequest
            {
                Id = Guid.NewGuid(),
                CompanyId = command.CompanyId,
                RequesterUserId = actor.UserId,
                RequestId = command.RequestId,
                Title = command.Title,
                Description = command.Description,
                AmountMinor = command.AmountMinor,
                Currency = command.Currency,
                DueDate = command.DueDate,
                BeneficiaryDisplayName = command.BeneficiaryDisplayName,
                ProofRequirement = command.ProofRequirement,
                State = "rascunho",
                Version = 1,
            };
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
            await transaction.CommitAsync(cancellationToken);
            return new PaguaiDraftResult(PaguaiDraftState.Created, draft);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            database.ChangeTracker.Clear();
            return await ReconcileReplayAsync(actor, command, hash, cancellationToken);
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
                "database_or_audit_unavailable"
            );
            return PaguaiDraftResult.Of(PaguaiDraftState.Unavailable);
        }
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

            return string.Equals(existing.PayloadHash, hash, StringComparison.Ordinal)
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
        CancellationToken cancellationToken
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
                    null,
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
        return requests.Select(ToDraft).ToList();
    }

    private static PaguaiDraft ToDraft(PaguaiRequest request) =>
        new(
            request.Id,
            request.CompanyId,
            request.RequestId,
            request.Title,
            request.Description,
            request.AmountMinor,
            request.Currency,
            request.DueDate,
            request.BeneficiaryDisplayName,
            request.ProofRequirement,
            request.State,
            request.Version,
            request.CreatedAt
        );

    private static PaguaiDraftResult DeserializeReplay(string resultJson) =>
        JsonSerializer.Deserialize<PaguaiDraft>(resultJson) is { } draft
            ? new PaguaiDraftResult(PaguaiDraftState.Replay, draft)
            : PaguaiDraftResult.Of(PaguaiDraftState.Unavailable);

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        if (exception.InnerException is not PostgresException postgresException)
        {
            return false;
        }

        return postgresException.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(
                postgresException.ConstraintName,
                IdempotencyRecordConfiguration.KeyIndexDatabaseName,
                StringComparison.Ordinal
            );
    }

    private static string ComputePayloadHash(CreateDraftCommand command)
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                companyId = command.CompanyId,
                requestId = command.RequestId,
                title = command.Title,
                description = command.Description,
                amountMinor = command.AmountMinor,
                currency = command.Currency,
                dueDate = command.DueDate,
                beneficiaryDisplayName = command.BeneficiaryDisplayName,
                proofRequirement = command.ProofRequirement,
            }
        );
        return Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private async Task ApplyContextAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    )
    {
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {actor.UserId.ToString()}, true)",
            cancellationToken
        );
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {companyId.ToString()}, true)",
            cancellationToken
        );
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.correlation_id', {actor.CorrelationId.ToString()}, true)",
            cancellationToken
        );
    }
}
