using System.Security.Claims;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class InternalIdentityResolverTests
{
    [Fact]
    public void Supabase_identity_provider_uses_only_issuer_and_subject()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("iss", "https://project.supabase.co/auth/v1"),
            new Claim("sub", "external-subject"),
            new Claim(ClaimTypes.Email, "same-address@example.test")
        ], "test"));

        var provider = new SupabaseIdentityProvider("https://project.supabase.co/auth/v1");

        Assert.True(provider.TryGetExternalIdentity(principal, out var key));
        Assert.Equal("https://project.supabase.co/auth/v1", key.Issuer);
        Assert.Equal("external-subject", key.Subject);
    }

    [Theory]
    [InlineData(null, "external-subject")]
    [InlineData("https://project.supabase.co/auth/v1", null)]
    public void Supabase_identity_provider_rejects_missing_external_identity_claims(
        string? issuer,
        string? subject)
    {
        var claims = new List<Claim>();
        if (issuer is not null)
        {
            claims.Add(new Claim("iss", issuer));
        }

        if (subject is not null)
        {
            claims.Add(new Claim("sub", subject));
        }

        var provider = new SupabaseIdentityProvider("https://project.supabase.co/auth/v1");

        Assert.False(provider.TryGetExternalIdentity(
            new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), out _));
    }
}
