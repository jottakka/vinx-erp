using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;

namespace Vinx.Erp.Foundation.Authorization;

public sealed class ActorMembershipMapService(
    AuthorizedCompanySetResolver resolver,
    AuthorizationTelemetry telemetry
)
{
    public async Task<
        AuthorizationResult<IReadOnlyList<MembershipCapabilitySummary>>
    > GetCurrentActorAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        var result = await resolver.ResolveSnapshotAsync(actor, cancellationToken);
        if (!result.IsSuccess)
        {
            telemetry.RecordAuditUnavailable(
                AuthorizationOperation.MembershipMap,
                AuditUnavailablePhase.Authorization);
            return AuthorizationResult<IReadOnlyList<MembershipCapabilitySummary>>.Failure(
                result.ErrorCode!.Value);
        }

        return AuthorizationResult<IReadOnlyList<MembershipCapabilitySummary>>.Success(
            result.Value!.Companies);
    }
}
