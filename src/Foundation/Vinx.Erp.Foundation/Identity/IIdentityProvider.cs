using System.Security.Claims;

namespace Vinx.Erp.Foundation.Identity;

public sealed record ExternalIdentityKey(string Issuer, string Subject);

public sealed record ExternalIdentityProof(
    ExternalIdentityKey Key,
    Guid ProviderSessionId,
    DateTimeOffset ProviderAuthenticatedAt,
    DateTimeOffset TokenExpiresAt);

public interface IIdentityProvider
{
    bool TryGetExternalIdentityProof(ClaimsPrincipal principal, out ExternalIdentityProof proof);
}
