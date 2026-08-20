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
using Vinx.Erp.Foundation.Persistence.Configurations;

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
            denial = await EvaluateAuthorizationAsync(actor, command.CompanyId, cancellationToken);
            if (denial is null)
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
#pragma warning disable CA1873 // Os argumentos são constantes ou valores baratos; o guard de logging não agrega ganho.
                    logger.LogInformation(
                        "Membership revocation {Operation} {Module} {Result} {Reason}",
                        Operation,
                        Module,
                        "revoked",
                        "active_membership_revoked");
#pragma warning restore CA1873
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
                denial = await EvaluateAuthorizationAsync(actor, command.CompanyId, cancellationToken);
                if (denial is null)
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

    private async Task<AuthorizationErrorCode?> EvaluateAuthorizationAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        var actorMembership = await database.Memberships.SingleOrDefaultAsync(membership =>
            membership.CompanyId == companyId && membership.UserId == actor.UserId,
            cancellationToken);
        if (actorMembership is null)
        {
            return AuthorizationErrorCode.MembershipRequired;
        }

        if (await capabilityEvaluator.HasGrantAsync(
                companyId,
                actorMembership.Id,
                RequiredCapability,
                cancellationToken))
        {
            return null;
        }

        return AuthorizationErrorCode.CapabilityDenied;
    }

    private static bool IsIdempotencyKeyConflict(DbUpdateException exception)
    {
        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException postgresException
                && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(
                    postgresException.ConstraintName,
                    IdempotencyRecordConfiguration.KeyIndexDatabaseName,
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

    private Task ApplyContextAsync(ActorContext actor, Guid companyId, CancellationToken cancellationToken) =>
        FoundationRlsContext.ApplyCommandAsync(database.Database, actor, companyId, cancellationToken);

    private static string ComputePayloadHash(RevokeMembershipCommand command)
    {
        var payload = string.Concat(
            command.CompanyId.ToString("N"),
            "|",
            command.MembershipId.ToString("N"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
