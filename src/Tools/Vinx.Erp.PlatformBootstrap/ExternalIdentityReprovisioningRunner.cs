using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.PlatformBootstrap;

public sealed class ExternalIdentityReprovisioningRunner(
    ExternalIdentityReprovisioningService reprovisioningService,
    IOptions<ExternalIdentityReprovisioningOptions> options
)
{
    public Task<ExternalIdentityReprovisioningState> RunAsync(CancellationToken cancellationToken)
    {
        var value = options.Value;
        return reprovisioningService.ReprovisionAsync(
            new ExternalIdentityReprovisioningCommand(
                Guid.Parse(value.UserId),
                Guid.Parse(value.ExpectedExternalIdentityId),
                value.ExpectedOldSubject,
                value.NewSubject,
                Guid.NewGuid()
            ),
            cancellationToken
        );
    }
}
