using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Platform;

namespace Vinx.Erp.Foundation.Bootstrap;

public sealed class FoundationBootstrapService(
    FoundationDbContext database,
    AuthorizedCompanySetResolver resolver,
    PlatformOperatorAccessService platformOperatorAccess,
    IOptions<FoundationBootstrapFeatureFlagsOptions> featureFlags,
    ILogger<FoundationBootstrapService> logger
)
{
    private const string Operation = "bootstrap_profile";
    private const string Module = "foundation";
    private const string EventTemplate =
        "Foundation bootstrap {Operation} {Module} {Result} {Reason}";

    private readonly IReadOnlyDictionary<string, bool> _effectiveFlags =
        featureFlags.Value.MaterializeEffectiveFlags();

    /// <summary>
    /// Materializa perfil próprio e snapshot de memberships sem aceitar contexto do cliente.
    /// </summary>
    public async Task<AuthorizationResult<FoundationBootstrapResponse>> GetCurrentActorAsync(
        ActorContext actor,
        CancellationToken cancellationToken
    )
    {
        var profile = await GetProfileAsync(actor, cancellationToken);
        if (!profile.IsSuccess)
        {
            return AuthorizationResult<FoundationBootstrapResponse>.Failure(
                profile.ErrorCode!.Value
            );
        }

        var snapshot = await resolver.ResolveSnapshotAsync(actor, cancellationToken);
        if (!snapshot.IsSuccess)
        {
            return AuthorizationResult<FoundationBootstrapResponse>.Failure(
                snapshot.ErrorCode!.Value
            );
        }

        var platform = await platformOperatorAccess.EvaluateAsync(actor, cancellationToken);
        if (platform.State is PlatformOperatorAccessState.Unavailable)
        {
            return AuthorizationResult<FoundationBootstrapResponse>.Failure(
                AuthorizationErrorCode.PlatformOperatorUnavailable
            );
        }

        return AuthorizationResult<FoundationBootstrapResponse>.Success(
            new FoundationBootstrapResponse(
                new FoundationBootstrapUser(profile.Value!.UserId, profile.Value.DisplayName),
                [.. snapshot
                    .Value!.Companies.Select(company => new FoundationBootstrapCompany(
                        company.MembershipId,
                        company.CompanyId,
                        company.CompanyName,
                        [.. company
                            .Capabilities.Select(capability => new FoundationBootstrapCapability(
                                capability.Name,
                                [.. capability
                                    .Scopes.OrderBy(scope => scope)
                                    .Select(scope => scope.ToString())]
                            ))]
                    ))],
                _effectiveFlags,
                new FoundationBootstrapPlatform(
                    platform.State is PlatformOperatorAccessState.Allowed
                )
            )
        );
    }

    private async Task<AuthorizationResult<CurrentActorProfile>> GetProfileAsync(
        ActorContext actor,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
            var profile = await database
                .Database.SqlQuery<CurrentActorProfile>(
                    $"SELECT user_id, display_name FROM foundation.current_actor_profile()"
                )
                .SingleOrDefaultAsync(cancellationToken);
            await transaction.RollbackAsync(cancellationToken);

            if (profile is not null)
            {
                return AuthorizationResult<CurrentActorProfile>.Success(profile);
            }

            logger.LogWarning(EventTemplate, Operation, Module, "unavailable", "profile_not_found");
            return AuthorizationResult<CurrentActorProfile>.Failure(
                AuthorizationErrorCode.AuditUnavailable
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
                EventTemplate,
                Operation,
                Module,
                "unavailable",
                "profile_lookup_failed"
            );
            return AuthorizationResult<CurrentActorProfile>.Failure(
                AuthorizationErrorCode.AuditUnavailable
            );
        }
    }

    private sealed record CurrentActorProfile(Guid UserId, string DisplayName);
}
