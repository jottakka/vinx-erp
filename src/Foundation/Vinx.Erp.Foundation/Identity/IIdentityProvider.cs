using System.Security.Claims;

namespace Vinx.Erp.Foundation.Identity;

public sealed record ExternalIdentityKey(string Issuer, string Subject);

public interface IIdentityProvider
{
    bool TryGetExternalIdentity(ClaimsPrincipal principal, out ExternalIdentityKey key);
}
