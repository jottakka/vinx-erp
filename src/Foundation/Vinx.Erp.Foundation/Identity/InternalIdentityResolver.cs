using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Identity;

public sealed class InternalIdentityResolver(FoundationDbContext database)
{
    public async Task<Guid?> ResolveAsync(ExternalIdentityKey key, CancellationToken cancellationToken)
    {
        return await database.ExternalIdentities
            .AsNoTracking()
            .Where(identity => identity.Issuer == key.Issuer && identity.Subject == key.Subject)
            .Select(identity => (Guid?)identity.UserId)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
