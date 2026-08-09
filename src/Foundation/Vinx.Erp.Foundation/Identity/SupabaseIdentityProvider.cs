using System.Security.Claims;

namespace Vinx.Erp.Foundation.Identity;

public sealed class SupabaseIdentityProvider(string issuer) : IIdentityProvider
{
    private readonly string _issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));

    public bool TryGetExternalIdentity(ClaimsPrincipal principal, out ExternalIdentityKey key)
    {
        key = null!;
        var issuer = principal.FindFirst("iss")?.Value;
        var subject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(issuer)
            || !string.Equals(issuer, _issuer, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        key = new ExternalIdentityKey(issuer, subject);
        return true;
    }
}
