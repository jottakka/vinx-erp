using System.Data;
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

            var draft = await InsertAsync(actor, command, cancellationToken);
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

            var draft = await ReadAsync(companyId, actor.UserId, id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return draft is null
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

    private async Task<PaguaiDraft> InsertAsync(
        ActorContext actor,
        CreateDraftCommand command,
        CancellationToken cancellationToken
    )
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var sql = connection.CreateCommand();
        sql.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        sql.CommandText = """
            INSERT INTO paguai.requests (id, company_id, requester_user_id, request_id, title, description)
            VALUES (@id, @companyId, @requesterUserId, @requestId, @title, @description)
            RETURNING id, company_id, request_id, title, description, state, version, created_at;
            """;
        AddParameter(sql, "id", Guid.NewGuid());
        AddParameter(sql, "companyId", command.CompanyId);
        AddParameter(sql, "requesterUserId", actor.UserId);
        AddParameter(sql, "requestId", command.RequestId);
        AddParameter(sql, "title", command.Title);
        AddParameter(sql, "description", command.Description);
        await using var reader = await sql.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Paguai insert returned no row.");
        }

        return ReadDraft(reader);
    }

    private async Task<PaguaiDraft?> ReadAsync(
        Guid companyId,
        Guid userId,
        Guid id,
        CancellationToken cancellationToken
    )
    {
        var connection = database.Database.GetDbConnection();
        await using var sql = connection.CreateCommand();
        sql.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        sql.CommandText = """
            SELECT id, company_id, request_id, title, description, state, version, created_at
            FROM paguai.requests
            WHERE company_id = @companyId AND requester_user_id = @userId AND id = @id
            """;
        AddParameter(sql, "companyId", companyId);
        AddParameter(sql, "userId", userId);
        AddParameter(sql, "id", id);
        await using var reader = await sql.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return ReadDraft(reader);
    }

    private async Task<IReadOnlyList<PaguaiDraft>> ReadListAsync(
        Guid companyId,
        Guid userId,
        CancellationToken cancellationToken
    )
    {
        var connection = database.Database.GetDbConnection();
        await using var sql = connection.CreateCommand();
        sql.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        sql.CommandText = """
            SELECT id, company_id, request_id, title, description, state, version, created_at
            FROM paguai.requests
            WHERE company_id = @companyId AND requester_user_id = @userId
            ORDER BY created_at DESC, id DESC
            """;
        AddParameter(sql, "companyId", companyId);
        AddParameter(sql, "userId", userId);
        await using var reader = await sql.ExecuteReaderAsync(cancellationToken);
        var drafts = new List<PaguaiDraft>();
        while (await reader.ReadAsync(cancellationToken))
            drafts.Add(ReadDraft(reader));
        return drafts;
    }

    private static PaguaiDraft ReadDraft(System.Data.Common.DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetFieldValue<DateTimeOffset>(7)
        );

    private static void AddParameter(
        System.Data.Common.DbCommand command,
        string name,
        object value
    )
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static PaguaiDraftResult DeserializeReplay(string resultJson) =>
        JsonSerializer.Deserialize<PaguaiDraft>(resultJson) is { } draft
            ? new PaguaiDraftResult(PaguaiDraftState.Replay, draft)
            : PaguaiDraftResult.Of(PaguaiDraftState.Unavailable);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException
            is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: IdempotencyRecordConfiguration.KeyIndexDatabaseName,
            };

    private static string ComputePayloadHash(CreateDraftCommand command)
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                companyId = command.CompanyId,
                requestId = command.RequestId,
                title = command.Title,
                description = command.Description,
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
