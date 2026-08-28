using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Identity;

/// <summary>
/// Vínculo interno do par de identidade externa que o servidor validou para a requisição.
/// </summary>
public sealed record ResolvedInternalIdentity(Guid UserId, Guid ExternalIdentityId);

public sealed class InternalIdentityResolver(FoundationDbContext database)
{
    public async Task<ResolvedInternalIdentity?> ResolveAsync(
        ExternalIdentityKey key,
        CancellationToken cancellationToken
    )
    {
        return await database
            .ExternalIdentities.AsNoTracking()
            .Where(identity => identity.Issuer == key.Issuer && identity.Subject == key.Subject)
            .Select(identity => new ResolvedInternalIdentity(identity.UserId, identity.Id))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
