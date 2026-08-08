using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Authorization;

public sealed class CapabilityMapService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    AuthorizationTelemetry telemetry)
{
    public async Task<AuthorizationResult<IReadOnlyList<EffectiveCapability>>> GetCapabilitiesAsync(
        ActorContext actor,
        Guid? companyId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (companyId is null)
            {
                return await ReturnAuditedDenialAsync(actor, null, AuthorizationErrorCode.CompanyContextRequired, cancellationToken);
            }

            AuthorizationErrorCode? denial = null;
            IReadOnlyList<EffectiveCapability>? capabilities = null;
            await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
            {
                await ApplyContextAsync(actor, companyId, cancellationToken);
                var membership = await database.Memberships
                    .AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.CompanyId == companyId && candidate.UserId == actor.UserId, cancellationToken);
                if (membership is null)
                {
                    denial = AuthorizationErrorCode.MembershipRequired;
                }
                else
                {
                    var grants = await (
                        from assignment in database.RoleAssignments.AsNoTracking()
                        join role in database.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                        join grant in database.RoleCapabilities.AsNoTracking() on role.Id equals grant.RoleId
                        join capability in database.Capabilities.AsNoTracking() on grant.CapabilityId equals capability.Id
                        where assignment.CompanyId == companyId
                            && assignment.MembershipId == membership.Id
                            && role.CompanyId == companyId
                            && grant.CompanyId == companyId
                        select new CapabilityGrant(capability.Name, grant.Scope))
                        .ToListAsync(cancellationToken);

                    if (grants.Count == 0)
                    {
                        denial = AuthorizationErrorCode.CapabilityDenied;
                    }
                    else
                    {
                        capabilities = grants
                            .GroupBy(grant => grant.Name, StringComparer.Ordinal)
                            .OrderBy(group => group.Key, StringComparer.Ordinal)
                            .Select(group => new EffectiveCapability(
                                group.Key,
                                new HashSet<CapabilityScope>(group.Select(grant => grant.Scope))))
                            .ToArray();
                    }
                }

                await transaction.RollbackAsync(cancellationToken);
            }

            if (denial is not null)
            {
                return await ReturnAuditedDenialAsync(actor, companyId, denial.Value, cancellationToken);
            }

            return AuthorizationResult<IReadOnlyList<EffectiveCapability>>.Success(capabilities!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return AuditUnavailable(AuditUnavailablePhase.Authorization);
        }
    }

    private async Task<AuthorizationResult<IReadOnlyList<EffectiveCapability>>> ReturnAuditedDenialAsync(
        ActorContext actor,
        Guid? companyId,
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
                "authorization",
                null,
                AuditOutcome.Denied,
                new CorrelationContext(actor.CorrelationId).CorrelationId,
                denial.ToStableCode(),
                "{}"), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AuthorizationResult<IReadOnlyList<EffectiveCapability>>.Failure(denial);
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

    private AuthorizationResult<IReadOnlyList<EffectiveCapability>> AuditUnavailable(AuditUnavailablePhase phase)
    {
        telemetry.RecordAuditUnavailable(AuthorizationOperation.CapabilityMap, phase);
        return AuthorizationResult<IReadOnlyList<EffectiveCapability>>.Failure(AuthorizationErrorCode.AuditUnavailable);
    }

    private async Task ApplyContextAsync(ActorContext actor, Guid? companyId, CancellationToken cancellationToken)
    {
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {actor.UserId.ToString()}, true)", cancellationToken);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {(companyId?.ToString() ?? string.Empty)}, true)", cancellationToken);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.correlation_id', {new CorrelationContext(actor.CorrelationId).CorrelationId.ToString()}, true)", cancellationToken);
    }

    private sealed record CapabilityGrant(string Name, CapabilityScope Scope);
}
