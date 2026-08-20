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
                    var grants = await CreateCapabilityGrantQuery(database, companyId.Value, membership.Id)
                        .ToListAsync(cancellationToken);

                    if (grants.Count == 0)
                    {
                        denial = AuthorizationErrorCode.CapabilityDenied;
                    }
                    else
                    {
                        capabilities = ProjectEffectiveCapabilities(grants);
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
                denial == AuthorizationErrorCode.CompanyContextRequired ? AuditScope.Global : AuditScope.Company,
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

    private Task ApplyContextAsync(ActorContext actor, Guid? companyId, CancellationToken cancellationToken) =>
        FoundationRlsContext.ApplyCommandAsync(database.Database, actor, companyId, cancellationToken);

    internal static IQueryable<CapabilityGrant> CreateCapabilityGrantQuery(
        FoundationDbContext database,
        Guid companyId,
        Guid membershipId) =>
        from assignment in database.RoleAssignments.AsNoTracking()
        join role in database.Roles.AsNoTracking() on assignment.RoleId equals role.Id
        join grant in database.RoleCapabilities.AsNoTracking() on role.Id equals grant.RoleId
        join capability in database.Capabilities.AsNoTracking() on grant.CapabilityId equals capability.Id
        where assignment.CompanyId == companyId
            && assignment.MembershipId == membershipId
            && role.CompanyId == companyId
            && grant.CompanyId == companyId
        select new CapabilityGrant(capability.Name, grant.Scope);

    internal static IReadOnlyList<EffectiveCapability> ProjectEffectiveCapabilities(IEnumerable<CapabilityGrant> grants) =>
        [.. grants
            .GroupBy(grant => grant.Name, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new EffectiveCapability(
                group.Key,
                new HashSet<CapabilityScope>(group.Select(grant => grant.Scope))))];

    internal sealed record CapabilityGrant(string Name, CapabilityScope Scope);
}
