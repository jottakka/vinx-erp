using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Platform;

namespace Vinx.Erp.PlatformBootstrap;

public enum PlatformBootstrapOutcome
{
    Created,
    AlreadyConfigured,
    Conflict,
    ProviderRejected,
    Unavailable,
}

public sealed class PlatformBootstrapRunner(
    SupabaseAdminUserVerifier verifier,
    PlatformOperatorBootstrapService bootstrapService,
    IOptions<PlatformOperatorDeploymentOptions> deploymentOptions,
    IOptions<SupabaseAuthOptions> authenticationOptions
)
{
    public async Task<PlatformBootstrapOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var deployment = deploymentOptions.Value;
        var verification = await verifier.VerifyAsync(deployment.Subject, cancellationToken);
        if (!verification.IsVerified || !verification.TryGetVerifiedEmail(out var providerEmail))
        {
            return verification.Category is PlatformOperatorVerificationCategory.ProviderUnavailable
                ? PlatformBootstrapOutcome.Unavailable
                : PlatformBootstrapOutcome.ProviderRejected;
        }

        var result = await bootstrapService.ExecuteAsync(
            new PlatformOperatorBootstrapCommand(
                deployment.Subject,
                authenticationOptions.Value.NormalizedIssuer,
                deployment.DisplayName,
                providerEmail,
                Guid.NewGuid()
            ),
            cancellationToken
        );
        return result switch
        {
            PlatformOperatorBootstrapState.Created => PlatformBootstrapOutcome.Created,
            PlatformOperatorBootstrapState.AlreadyConfigured =>
                PlatformBootstrapOutcome.AlreadyConfigured,
            PlatformOperatorBootstrapState.Conflict => PlatformBootstrapOutcome.Conflict,
            PlatformOperatorBootstrapState.Unavailable => PlatformBootstrapOutcome.Unavailable,
            _ => PlatformBootstrapOutcome.Unavailable,
        };
    }
}
