using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Authorization;

public sealed class ActorMembershipMapService(
    FoundationDbContext database,
    AuthorizationTelemetry telemetry,
    ILogger<ActorMembershipMapService> logger
)
{
    private const string Operation = "membership_map";
    private const string Module = "authorization";
    private const string UnavailableResult = "unavailable";
    private const string UnavailableReason = "discovery_or_projection_failed";
    private const string UnavailableEventTemplate =
        "Membership map {Operation} {Module} {Result} {Reason}";

    public async Task<
        AuthorizationResult<IReadOnlyList<MembershipCapabilitySummary>>
    > GetCurrentActorAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await ApplyDiscoveryContextAsync(actor, cancellationToken);
            var memberships = await database
                .Database.SqlQuery<DiscoveredMembership>(
                    $"SELECT membership_id, company_id, company_name FROM foundation.current_actor_memberships()"
                )
                .ToListAsync(cancellationToken);
            var summaries = new List<MembershipCapabilitySummary>(memberships.Count);

            foreach (var membership in memberships)
            {
                await SetCompanyContextAsync(membership.CompanyId, cancellationToken);
                var grants = await CapabilityMapService
                    .CreateCapabilityGrantQuery(
                        database,
                        membership.CompanyId,
                        membership.MembershipId
                    )
                    .ToListAsync(cancellationToken);
                summaries.Add(
                    new MembershipCapabilitySummary(
                        membership.MembershipId,
                        membership.CompanyId,
                        membership.CompanyName,
                        CapabilityMapService.ProjectEffectiveCapabilities(grants)
                    )
                );
            }

            await transaction.RollbackAsync(cancellationToken);
            return AuthorizationResult<IReadOnlyList<MembershipCapabilitySummary>>.Success(
                summaries
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
                UnavailableEventTemplate,
                Operation,
                Module,
                UnavailableResult,
                UnavailableReason
            );
            telemetry.RecordAuditUnavailable(
                AuthorizationOperation.MembershipMap,
                AuditUnavailablePhase.Authorization
            );
            return AuthorizationResult<IReadOnlyList<MembershipCapabilitySummary>>.Failure(
                AuthorizationErrorCode.AuditUnavailable
            );
        }
    }

    private async Task ApplyDiscoveryContextAsync(
        ActorContext actor,
        CancellationToken cancellationToken
    )
    {
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {actor.UserId.ToString()}, true)",
            cancellationToken
        );
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {string.Empty}, true)",
            cancellationToken
        );
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.correlation_id', {new CorrelationContext(actor.CorrelationId).CorrelationId.ToString()}, true)",
            cancellationToken
        );
    }

    private Task SetCompanyContextAsync(Guid companyId, CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {companyId.ToString()}, true)",
            cancellationToken
        );

    private sealed record DiscoveredMembership(
        Guid MembershipId,
        Guid CompanyId,
        string CompanyName
    );
}
