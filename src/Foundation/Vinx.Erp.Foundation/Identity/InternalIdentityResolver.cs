using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Identity;

public sealed class InternalIdentityResolver(
    FoundationDbContext database,
    IIdentityProvider identityProvider)
{
    public async Task<Guid?> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!identityProvider.TryGetExternalIdentity(principal, out var key))
        {
            return null;
        }

        return await database.ExternalIdentities
            .AsNoTracking()
            .Where(identity => identity.Issuer == key.Issuer && identity.Subject == key.Subject)
            .Select(identity => (Guid?)identity.UserId)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
