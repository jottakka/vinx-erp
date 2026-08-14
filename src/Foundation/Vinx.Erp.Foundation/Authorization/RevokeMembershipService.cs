using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Authorization;

public sealed class RevokeMembershipService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    AuthorizationTelemetry telemetry,
    CapabilityEvaluator capabilityEvaluator,
    ILogger<RevokeMembershipService> logger)
{
    private const string CommandName = "foundation.memberships.revoke";
    private const string Operation = "membership_revocation";
    private const string Module = "foundation";
    private const string RevocationReason = "membership_revoked";
    private const string IdempotencyRecordKeyIndexName =
        "ix_idempotency_records_company_id_actor_user_id_command_name_r";
    private static readonly CapabilityRequirement RequiredCapability =
        new(KnownCapabilities.MembershipsRevoke, CapabilityScope.Company);

    public async Task<AuthorizationResult<RevokeMembershipResult>> RevokeAsync(
        ActorContext actor,
        RevokeMembershipCommand command,
        CancellationToken cancellationToken)
    {
        var payloadHash = ComputePayloadHash(command);
        AuthorizationErrorCode? denial = null;

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            var actorMembership = await database.Memberships.SingleOrDefaultAsync(membership =>
                membership.CompanyId == command.CompanyId && membership.UserId == actor.UserId,
                cancellationToken);
            if (actorMembership is null)
            {
                denial = AuthorizationErrorCode.MembershipRequired;
            }
            else if (!await capabilityEvaluator.HasGrantAsync(
                         command.CompanyId,
                         actorMembership.Id,
                         RequiredCapability,
                         cancellationToken))
            {
                denial = AuthorizationErrorCode.CapabilityDenied;
            }
            else
            {
                var existing = await database.IdempotencyRecords.SingleOrDefaultAsync(record =>
                    record.CompanyId == command.CompanyId
                    && record.ActorUserId == actor.UserId
                    && record.CommandName == CommandName
                    && record.RequestId == command.RequestId,
                    cancellationToken);
                if (existing is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                    {
                        return await ReturnAuditedDenialAsync(
                            actor,
                            command.CompanyId,
                            AuthorizationErrorCode.IdempotencyConflict,
                            cancellationToken);
                    }

                    var replay = JsonSerializer.Deserialize<RevokeMembershipResult>(existing.ResultJson);
                    return replay is null
                        ? AuditUnavailable(AuditUnavailablePhase.IdempotencyReplay)
                        : AuthorizationResult<RevokeMembershipResult>.Success(replay);
                }

                var targetMembership = await database.Memberships.SingleOrDefaultAsync(candidate =>
                    candidate.CompanyId == command.CompanyId && candidate.Id == command.MembershipId,
                    cancellationToken);
                if (targetMembership is null)
                {
                    denial = AuthorizationErrorCode.ResourceNotFound;
                }
                else
                {
                    targetMembership.Revoke(RevocationReason, actor.UserId, command.RequestId);
                    var result = new RevokeMembershipResult(targetMembership.Id, true);
                    database.IdempotencyRecords.Add(new IdempotencyRecord(
                        Guid.NewGuid(),
                        command.CompanyId,
                        actor.UserId,
                        CommandName,
                        command.RequestId,
                        payloadHash,
                        JsonSerializer.Serialize(result)));
                    await auditAppender.AppendAsync(new AuditEventData(
                        actor.UserId,
                        AuditScope.Company,
                        command.CompanyId,
                        AuditSource.Web,
                        CommandName,
                        "membership",
                        targetMembership.Id,
                        AuditOutcome.Success,
                        new CorrelationContext(actor.CorrelationId).CorrelationId,
                        null,
                        "{}"), cancellationToken);
                    await database.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    logger.LogInformation(
                        "Membership revocation {Operation} {Module} {Result} {Reason}",
                        Operation,
                        Module,
                        "revoked",
                        "active_membership_revoked");
                    return AuthorizationResult<RevokeMembershipResult>.Success(result);
                }
            }

            await transaction.RollbackAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            logger.LogWarning(
                exception,
                "Membership revocation {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "retrying",
                "concurrent_write");
            database.ChangeTracker.Clear();
            return await ReturnConcurrentReplayAsync(actor, command, payloadHash, cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            database.ChangeTracker.Clear();
            return AuditUnavailable(AuditUnavailablePhase.SuccessTransaction, exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            return AuditUnavailable(AuditUnavailablePhase.SuccessTransaction, exception);
        }

        return await ReturnAuditedDenialAsync(actor, command.CompanyId, denial!.Value, cancellationToken);
    }

    private async Task<AuthorizationResult<RevokeMembershipResult>> ReturnAuditedDenialAsync(
        ActorContext actor,
        Guid companyId,
        AuthorizationErrorCode denial,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, companyId, cancellationToken);
            await auditAppender.AppendAsync(new AuditEventData(
                actor.UserId,
                AuditScope.Company,
                companyId,
                AuditSource.Web,
                denial.ToStableCode(),
                "membership",
                null,
                AuditOutcome.Denied,
                new CorrelationContext(actor.CorrelationId).CorrelationId,
                denial.ToStableCode(),
                "{}"), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AuthorizationResult<RevokeMembershipResult>.Failure(denial);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return AuditUnavailable(AuditUnavailablePhase.DenialAudit, exception);
        }
    }

    private async Task<AuthorizationResult<RevokeMembershipResult>> ReturnConcurrentReplayAsync(
        ActorContext actor,
        RevokeMembershipCommand command,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        try
        {
            IdempotencyRecord? record = null;
            AuthorizationErrorCode? denial = null;
            await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
            {
                await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
                var actorMembership = await database.Memberships.SingleOrDefaultAsync(membership =>
                    membership.CompanyId == command.CompanyId && membership.UserId == actor.UserId,
                    cancellationToken);
                if (actorMembership is null)
                {
                    denial = AuthorizationErrorCode.MembershipRequired;
                }
                else if (!await capabilityEvaluator.HasGrantAsync(
                             command.CompanyId,
                             actorMembership.Id,
                             RequiredCapability,
                             cancellationToken))
                {
                    denial = AuthorizationErrorCode.CapabilityDenied;
                }
                else
                {
                    record = await database.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(candidate =>
                        candidate.CompanyId == command.CompanyId
                        && candidate.ActorUserId == actor.UserId
                        && candidate.CommandName == CommandName
                        && candidate.RequestId == command.RequestId,
                        cancellationToken);
                }

                await transaction.RollbackAsync(cancellationToken);
            }

            if (denial is not null)
            {
                return await ReturnAuditedDenialAsync(
                    actor,
                    command.CompanyId,
                    denial.Value,
                    cancellationToken);
            }

            if (record is null)
            {
                return await ReturnAuditedDenialAsync(
                    actor,
                    command.CompanyId,
                    AuthorizationErrorCode.IntegrityConflict,
                    cancellationToken);
            }

            if (!string.Equals(record.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return await ReturnAuditedDenialAsync(
                    actor,
                    command.CompanyId,
                    AuthorizationErrorCode.IdempotencyConflict,
                    cancellationToken);
            }

            var replay = JsonSerializer.Deserialize<RevokeMembershipResult>(record.ResultJson);
            return replay is null
                ? AuditUnavailable(AuditUnavailablePhase.ConcurrentReplay)
                : AuthorizationResult<RevokeMembershipResult>.Success(replay);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return AuditUnavailable(AuditUnavailablePhase.ConcurrentReplay, exception);
        }
    }

    private static bool IsIdempotencyKeyConflict(DbUpdateException exception)
    {
        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException postgresException
                && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(
                    postgresException.ConstraintName,
                    IdempotencyRecordKeyIndexName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private AuthorizationResult<RevokeMembershipResult> AuditUnavailable(
        AuditUnavailablePhase phase,
        Exception? exception = null)
    {
        logger.LogError(
            exception,
            "Membership revocation {Operation} {Module} {Result} {Reason}",
            Operation,
            Module,
            "unavailable",
            "database_or_audit_unavailable");
        telemetry.RecordAuditUnavailable(AuthorizationOperation.MembershipRevocation, phase);
        return AuthorizationResult<RevokeMembershipResult>.Failure(AuthorizationErrorCode.AuditUnavailable);
    }

    private async Task ApplyContextAsync(ActorContext actor, Guid companyId, CancellationToken cancellationToken)
    {
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {actor.UserId.ToString()}, true)", cancellationToken);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {companyId.ToString()}, true)", cancellationToken);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.correlation_id', {new CorrelationContext(actor.CorrelationId).CorrelationId.ToString()}, true)", cancellationToken);
    }

    private static string ComputePayloadHash(RevokeMembershipCommand command)
    {
        var payload = string.Concat(
            command.CompanyId.ToString("N"),
            "|",
            command.MembershipId.ToString("N"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
