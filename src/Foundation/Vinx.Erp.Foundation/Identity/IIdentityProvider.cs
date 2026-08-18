using System.Security.Claims;

namespace Vinx.Erp.Foundation.Identity;

public sealed record ExternalIdentityKey(string Issuer, string Subject);

public enum AuthenticatorAssuranceLevel
{
    Aal1,
    Aal2,
}

public sealed record ExternalIdentityProof(
    ExternalIdentityKey Key,
    Guid ProviderSessionId,
    DateTimeOffset ProviderAuthenticatedAt,
    DateTimeOffset TokenExpiresAt,
    AuthenticatorAssuranceLevel AssuranceLevel = AuthenticatorAssuranceLevel.Aal1);

public interface IIdentityProvider
{
    bool TryGetExternalIdentityProof(ClaimsPrincipal principal, out ExternalIdentityProof proof);
}
