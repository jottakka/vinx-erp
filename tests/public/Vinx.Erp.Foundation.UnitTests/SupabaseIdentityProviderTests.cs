using System.Globalization;
using System.Security.Claims;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class SupabaseIdentityProviderTests
{
    private const string Issuer = "https://project.supabase.co/auth/v1";
    private static readonly Guid SessionId = Guid.Parse("4f121c73-c8a2-4639-83f1-561f936b4977");

    [Fact]
    public void Try_get_external_identity_proof_uses_earliest_non_refresh_amr_timestamp()
    {
        var principal = PrincipalWith(
        [
            Amr("token_refresh", 140),
            Amr("password", 100),
            Amr("totp", 99)
        ]);

        var result = Provider().TryGetExternalIdentityProof(principal, out var proof);

        Assert.True(result);
        Assert.Equal("https://project.supabase.co/auth/v1", proof.Key.Issuer);
        Assert.Equal("external-subject", proof.Key.Subject);
        Assert.Equal(SessionId, proof.ProviderSessionId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(99), proof.ProviderAuthenticatedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(101), proof.TokenExpiresAt);
    }

    [Fact]
    public void Try_get_external_identity_proof_ignores_email_and_correlation_claims()
    {
        var principal = PrincipalWith(
            [Amr("passkey", 100)],
            new Claim(ClaimTypes.Email, "same-address@example.test"),
            new Claim("correlation_id", "request-correlation"));

        var result = Provider().TryGetExternalIdentityProof(principal, out var proof);

        Assert.True(result);
        Assert.Equal("external-subject", proof.Key.Subject);
    }

    [Fact]
    public void Try_get_external_identity_proof_accepts_an_unknown_non_refresh_authentication_method()
    {
        var result = Provider().TryGetExternalIdentityProof(
            PrincipalWith([Amr("future_authenticator", 100)]), out var proof);

        Assert.True(result);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(100), proof.ProviderAuthenticatedAt);
    }

    [Theory]
    [MemberData(nameof(InvalidRequiredScalarClaims))]
    public void Try_get_external_identity_proof_rejects_missing_duplicate_or_invalid_required_scalar_claims(
        string claimType,
        string? value,
        bool duplicate)
    {
        var claims = ValidClaims();
        claims.RemoveAll(claim => claim.Type == claimType);
        if (value is not null)
        {
            claims.Add(new Claim(claimType, value));
        }

        if (duplicate)
        {
            claims.Add(new Claim(claimType, value ?? "duplicate"));
        }

        Assert.False(Provider().TryGetExternalIdentityProof(Principal(claims), out _));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("4f121c73c8a2463983f1561f936b4977")]
    [InlineData("{4f121c73-c8a2-4639-83f1-561f936b4977}")]
    [InlineData("")]
    public void Try_get_external_identity_proof_rejects_non_canonical_or_empty_session_id(string sessionId)
    {
        var claims = ValidClaims();
        ReplaceScalar(claims, "session_id", sessionId);

        Assert.False(Provider().TryGetExternalIdentityProof(Principal(claims), out _));
    }

    [Theory]
    [InlineData("10.5")]
    [InlineData("9223372036854775808")]
    public void Try_get_external_identity_proof_rejects_non_integer_or_out_of_range_token_times(string value)
    {
        foreach (var claimType in new[] { "iat", "exp" })
        {
            var claims = ValidClaims();
            ReplaceScalar(claims, claimType, value);

            Assert.False(Provider().TryGetExternalIdentityProof(Principal(claims), out _));
        }
    }

    [Theory]
    [InlineData(100L)]
    [InlineData(99L)]
    public void Try_get_external_identity_proof_rejects_non_positive_token_lifetime(long expiresAt)
    {
        var claims = ValidClaims();
        ReplaceScalar(claims, "exp", expiresAt.ToString(CultureInfo.InvariantCulture));

        Assert.False(Provider().TryGetExternalIdentityProof(Principal(claims), out _));
    }

    [Theory]
    [MemberData(nameof(InvalidAmrClaims))]
    public void Try_get_external_identity_proof_rejects_malformed_or_ambiguous_amr_claims(Claim[] amrClaims)
    {
        var claims = ValidClaims();
        claims.RemoveAll(claim => claim.Type == "amr");
        claims.AddRange(amrClaims);

        Assert.False(Provider().TryGetExternalIdentityProof(Principal(claims), out _));
    }

    [Fact]
    public void Try_get_external_identity_proof_rejects_when_authentication_is_later_than_issue_time()
    {
        Assert.False(Provider().TryGetExternalIdentityProof(
            PrincipalWith([Amr("password", 101)]), out _));
    }

    [Fact]
    public void Try_get_external_identity_proof_rejects_an_unrepresentable_refresh_timestamp()
    {
        Assert.False(Provider().TryGetExternalIdentityProof(
            PrincipalWith([Amr("token_refresh", long.MaxValue), Amr("password", 100)]), out _));
    }

    public static TheoryData<string, string?, bool> InvalidRequiredScalarClaims() =>
        new()
        {
            { "iss", null, false },
            { "sub", null, false },
            { "session_id", null, false },
            { "iat", null, false },
            { "exp", null, false },
            { "iss", "", false },
            { "sub", " ", false },
            { "iss", "https://other.supabase.co/auth/v1", false },
            { "iss", Issuer, true },
            { "sub", "external-subject", true },
            { "session_id", SessionId.ToString("D"), true },
            { "iat", "100", true },
            { "exp", "101", true }
        };

    public static TheoryData<Claim[]> InvalidAmrClaims() =>
        new()
        {
            { [new Claim("amr", "password")] },
            { [Amr("password", 100), new Claim("amr", "totp")] },
            { [JsonAmr("not-json")] },
            { [JsonAmr("[]")] },
            { [JsonAmr("{\"timestamp\":100}")] },
            { [JsonAmr("{\"method\":\"\",\"timestamp\":100}")] },
            { [JsonAmr("{\"method\":\"password\",\"method\":\"totp\",\"timestamp\":100}")] },
            { [JsonAmr("{\"method\":\"password\"}")] },
            { [JsonAmr("{\"method\":\"password\",\"timestamp\":100.5}")] },
            { [JsonAmr("{\"method\":\"password\",\"timestamp\":100,\"timestamp\":101}")] },
            { [JsonAmr("{\"method\":\"password\",\"timestamp\":9223372036854775808}")] },
            { [Amr("token_refresh", 100)] }
        };

    private static SupabaseIdentityProvider Provider() => new(Issuer);

    private static ClaimsPrincipal PrincipalWith(IEnumerable<Claim> amrClaims, params Claim[] extraClaims)
    {
        var claims = ValidClaims();
        claims.RemoveAll(claim => claim.Type == "amr");
        claims.AddRange(amrClaims);
        claims.AddRange(extraClaims);
        return Principal(claims);
    }

    private static List<Claim> ValidClaims() =>
    [
        new Claim("iss", Issuer),
        new Claim("sub", "external-subject"),
        new Claim("session_id", SessionId.ToString("D")),
        new Claim("iat", "100", ClaimValueTypes.Integer64),
        new Claim("exp", "101", ClaimValueTypes.Integer64),
        Amr("password", 100)
    ];

    private static Claim Amr(string method, long timestamp) =>
        JsonAmr($"{{\"method\":\"{method}\",\"timestamp\":{timestamp.ToString(CultureInfo.InvariantCulture)}}}");

    private static Claim JsonAmr(string value) => new("amr", value, "JSON");

    private static ClaimsPrincipal Principal(IEnumerable<Claim> claims) =>
        new(new ClaimsIdentity(claims, "test"));

    private static void ReplaceScalar(List<Claim> claims, string claimType, string value)
    {
        claims.RemoveAll(claim => claim.Type == claimType);
        claims.Add(new Claim(claimType, value));
    }
}
