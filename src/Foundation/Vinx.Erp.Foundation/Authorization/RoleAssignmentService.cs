using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Authorization;

public sealed class RoleAssignmentService(FoundationDbContext database, AuditAppender auditAppender)
{
    private const string CommandName = "foundation.roles.assign";
    private const string RequiredCapability = "foundation.roles.assign";

    public async Task<AuthorizationResult<AssignRoleToMembershipResult>> AssignRoleToMembershipAsync(
        ActorContext actor,
        AssignRoleToMembershipCommand command,
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
            else if (!await HasRequiredCapabilityAsync(command.CompanyId, actorMembership.Id, cancellationToken))
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
                        return await ReturnAuditedDenialAsync(actor, command.CompanyId, AuthorizationErrorCode.IdempotencyConflict, cancellationToken);
                    }

                    var replay = JsonSerializer.Deserialize<AssignRoleToMembershipResult>(existing.ResultJson);
                    return replay is null
                        ? AuthorizationResult<AssignRoleToMembershipResult>.Failure(AuthorizationErrorCode.AuditUnavailable)
                        : AuthorizationResult<AssignRoleToMembershipResult>.Success(replay);
                }
                else
                {
                    var role = await database.Roles.SingleOrDefaultAsync(candidate =>
                        candidate.CompanyId == command.CompanyId && candidate.Id == command.RoleId,
                        cancellationToken);
                    var targetMembership = await database.Memberships.SingleOrDefaultAsync(candidate =>
                        candidate.CompanyId == command.CompanyId && candidate.Id == command.MembershipId,
                        cancellationToken);
                    if (role is null || targetMembership is null)
                    {
                        denial = AuthorizationErrorCode.ResourceNotFound;
                    }
                    else
                    {
                        var assignment = new RoleAssignment(
                            Guid.NewGuid(),
                            command.CompanyId,
                            command.RoleId,
                            command.MembershipId,
                            actor.UserId,
                            command.RequestId);
                        var result = new AssignRoleToMembershipResult(assignment.Id);
                        database.RoleAssignments.Add(assignment);
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
                        return AuthorizationResult<AssignRoleToMembershipResult>.Success(result);
                    }
                }
            }

            await transaction.RollbackAsync(cancellationToken);
        }
        catch (DbUpdateException)
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
            return AuthorizationResult<AssignRoleToMembershipResult>.Failure(AuthorizationErrorCode.AuditUnavailable);
        }

        return await ReturnAuditedDenialAsync(actor, command.CompanyId, denial!.Value, cancellationToken);
    }

    private async Task<bool> HasRequiredCapabilityAsync(Guid companyId, Guid membershipId, CancellationToken cancellationToken) =>
        await (
            from assignment in database.RoleAssignments.AsNoTracking()
            join role in database.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            join grant in database.RoleCapabilities.AsNoTracking() on role.Id equals grant.RoleId
            join capability in database.Capabilities.AsNoTracking() on grant.CapabilityId equals capability.Id
            where assignment.CompanyId == companyId
                && assignment.MembershipId == membershipId
                && role.CompanyId == companyId
                && grant.CompanyId == companyId
                && capability.Name == RequiredCapability
            select capability.Id)
            .AnyAsync(cancellationToken);

    private async Task<AuthorizationResult<AssignRoleToMembershipResult>> ReturnAuditedDenialAsync(
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
            return AuthorizationResult<AssignRoleToMembershipResult>.Failure(denial);
        }
        catch
        {
            return AuthorizationResult<AssignRoleToMembershipResult>.Failure(AuthorizationErrorCode.AuditUnavailable);
        }
    }

    private async Task<AuthorizationResult<AssignRoleToMembershipResult>> ReturnConcurrentReplayAsync(
        ActorContext actor,
        AssignRoleToMembershipCommand command,
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
                return AuthorizationResult<AssignRoleToMembershipResult>.Failure(AuthorizationErrorCode.AuditUnavailable);
            }

            if (!string.Equals(record.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return await ReturnAuditedDenialAsync(actor, command.CompanyId, AuthorizationErrorCode.IdempotencyConflict, cancellationToken);
            }

            var replay = JsonSerializer.Deserialize<AssignRoleToMembershipResult>(record.ResultJson);
            return replay is null
                ? AuthorizationResult<AssignRoleToMembershipResult>.Failure(AuthorizationErrorCode.AuditUnavailable)
                : AuthorizationResult<AssignRoleToMembershipResult>.Success(replay);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return AuthorizationResult<AssignRoleToMembershipResult>.Failure(AuthorizationErrorCode.AuditUnavailable);
        }
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

    private static string ComputePayloadHash(AssignRoleToMembershipCommand command)
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
