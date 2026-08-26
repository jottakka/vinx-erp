using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Configurations;

namespace Vinx.Erp.Foundation.Authorization;

/// <summary>
/// Revoga (soft-delete) a atribuição viva de um papel a uma membresia. Antes de
/// preparar a mutação, assume advisory lock transacional por empresa e aplica o
/// bloqueio de último administrador: a revogação é negada quando deixaria a
/// empresa sem nenhuma membresia ativa com grant de atribuição de papéis.
/// </summary>
public sealed class RoleUnassignmentService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    AuthorizationTelemetry telemetry,
    CapabilityEvaluator capabilityEvaluator)
{
    private const string CommandName = "foundation.roles.unassign";
    private const string RevocationReason = "role_unassigned";
    private static readonly CapabilityRequirement RequiredCapability =
        new(KnownCapabilities.RolesAssign, CapabilityScope.Company);

    public async Task<AuthorizationResult<UnassignRoleFromMembershipResult>> UnassignRoleFromMembershipAsync(
        ActorContext actor,
        UnassignRoleFromMembershipCommand command,
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
                // Serializa rebaixamentos concorrentes de administrador na mesma empresa
                // antes de qualquer leitura de estado ou preparo de mutação.
                await database.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({command.CompanyId.ToString()}, 0))",
                    cancellationToken);
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

                    var replay = JsonSerializer.Deserialize<UnassignRoleFromMembershipResult>(existing.ResultJson);
                    return replay is null
                        ? AuditUnavailable(AuditUnavailablePhase.IdempotencyReplay)
                        : AuthorizationResult<UnassignRoleFromMembershipResult>.Success(replay);
                }

                // O query filter global já restringe às atribuições vivas.
                var assignment = await database.RoleAssignments.SingleOrDefaultAsync(candidate =>
                    candidate.CompanyId == command.CompanyId
                    && candidate.MembershipId == command.MembershipId
                    && candidate.RoleId == command.RoleId,
                    cancellationToken);
                if (assignment is null)
                {
                    denial = AuthorizationErrorCode.ResourceNotFound;
                }
                else if (
                    !await LastAdminGuard.CompanyRetainsRolesAssignAdminAsync(
                        database, command.CompanyId, command.MembershipId, cancellationToken)
                    && !await LastAdminGuard.MembershipRetainsRolesAssignAdminAsync(
                        database, command.CompanyId, command.MembershipId, assignment.Id, cancellationToken))
                {
                    denial = AuthorizationErrorCode.LastAdminGuard;
                }
                else
                {
                    assignment.Revoke(RevocationReason, actor.UserId, command.RequestId);
                    var result = new UnassignRoleFromMembershipResult(assignment.Id, true);
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
                        "role_assignment",
                        assignment.Id,
                        AuditOutcome.Success,
                        new CorrelationContext(actor.CorrelationId).CorrelationId,
                        null,
                        "{}"), cancellationToken);
                    await database.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return AuthorizationResult<UnassignRoleFromMembershipResult>.Success(result);
                }
            }

            await transaction.RollbackAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            database.ChangeTracker.Clear();
            return await ReturnConcurrentReplayAsync(actor, command, payloadHash, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            database.ChangeTracker.Clear();
            return AuditUnavailable(AuditUnavailablePhase.SuccessTransaction);
        }

        return await ReturnAuditedDenialAsync(actor, command.CompanyId, denial!.Value, cancellationToken);
    }

    private async Task<AuthorizationResult<UnassignRoleFromMembershipResult>> ReturnAuditedDenialAsync(
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
                "role_assignment",
                null,
                AuditOutcome.Denied,
                new CorrelationContext(actor.CorrelationId).CorrelationId,
                denial.ToStableCode(),
                "{}"), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AuthorizationResult<UnassignRoleFromMembershipResult>.Failure(denial);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return AuditUnavailable(AuditUnavailablePhase.DenialAudit);
        }
    }

    private async Task<AuthorizationResult<UnassignRoleFromMembershipResult>> ReturnConcurrentReplayAsync(
        ActorContext actor,
        UnassignRoleFromMembershipCommand command,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            var record = await database.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(candidate =>
                candidate.CompanyId == command.CompanyId
                && candidate.ActorUserId == actor.UserId
                && candidate.CommandName == CommandName
                && candidate.RequestId == command.RequestId,
                cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
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
                return await ReturnAuditedDenialAsync(actor, command.CompanyId, AuthorizationErrorCode.IdempotencyConflict, cancellationToken);
            }

            var replay = JsonSerializer.Deserialize<UnassignRoleFromMembershipResult>(record.ResultJson);
            return replay is null
                ? AuditUnavailable(AuditUnavailablePhase.ConcurrentReplay)
                : AuthorizationResult<UnassignRoleFromMembershipResult>.Success(replay);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return AuditUnavailable(AuditUnavailablePhase.ConcurrentReplay);
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

    private AuthorizationResult<UnassignRoleFromMembershipResult> AuditUnavailable(AuditUnavailablePhase phase)
    {
        telemetry.RecordAuditUnavailable(AuthorizationOperation.RoleUnassignment, phase);
        return AuthorizationResult<UnassignRoleFromMembershipResult>.Failure(AuthorizationErrorCode.AuditUnavailable);
    }

    private Task ApplyContextAsync(ActorContext actor, Guid companyId, CancellationToken cancellationToken) =>
        FoundationRlsContext.ApplyCommandAsync(database.Database, actor, companyId, cancellationToken);

    private static string ComputePayloadHash(UnassignRoleFromMembershipCommand command)
    {
        var payload = string.Concat(
            command.CompanyId.ToString("N"),
            "|",
            command.RoleId.ToString("N"),
            "|",
            command.MembershipId.ToString("N"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
