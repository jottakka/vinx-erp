using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class SupabaseAccessTokenValidatorTests
{
    private const string Issuer = "https://project.supabase.co/auth/v1";
    private const string Audience = "authenticated";
    private static readonly byte[] SigningKey = Encoding.UTF8.GetBytes(
        "test-signing-key-that-is-long-enough-for-hs256");

    [Fact]
    public async Task ValidateAsync_returns_valid_and_principal_for_a_signed_token()
    {
        var options = Options();
        var validator = new SupabaseAccessTokenValidator(options);

        var result = await validator.ValidateAsync(Token(), CancellationToken.None);

        Assert.Equal(SupabaseAccessTokenValidationState.Valid, result.State);
        Assert.NotNull(result.Principal);
        Assert.Equal(TimeSpan.FromMinutes(1), options.TokenValidationParameters.ClockSkew);
        Assert.Equal("user-123", result.Principal.FindFirstValue(JwtRegisteredClaimNames.Sub));
        Assert.Equal("alice@example.test", result.Principal.FindFirstValue("email"));
    }

    [Fact]
    public async Task ValidateAsync_returns_invalid_for_a_bad_signature()
    {
        var validator = new SupabaseAccessTokenValidator(Options());
        var token = Token(SigningKey: Encoding.UTF8.GetBytes("a-different-signing-key-that-is-long-enough"));

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.Equal(SupabaseAccessTokenValidationState.Invalid, result.State);
        Assert.Null(result.Principal);
    }

    [Fact]
    public async Task ValidateAsync_returns_invalid_for_a_wrong_issuer_or_audience()
    {
        var validator = new SupabaseAccessTokenValidator(Options());

        var wrongIssuer = await validator.ValidateAsync(
            Token(issuer: "https://other.supabase.co/auth/v1"), CancellationToken.None);
        var wrongAudience = await validator.ValidateAsync(
            Token(audience: "other-audience"), CancellationToken.None);

        Assert.Equal(SupabaseAccessTokenValidationState.Invalid, wrongIssuer.State);
        Assert.Null(wrongIssuer.Principal);
        Assert.Equal(SupabaseAccessTokenValidationState.Invalid, wrongAudience.State);
        Assert.Null(wrongAudience.Principal);
    }

    [Fact]
    public async Task ValidateAsync_returns_invalid_for_an_expired_token()
    {
        var validator = new SupabaseAccessTokenValidator(Options());

        var result = await validator.ValidateAsync(
            Token(expires: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            CancellationToken.None);

        Assert.Equal(SupabaseAccessTokenValidationState.Invalid, result.State);
        Assert.Null(result.Principal);
    }

    [Fact]
    public async Task ValidateAsync_returns_unavailable_when_configuration_cannot_be_loaded()
    {
        var options = Options(new ThrowingConfigurationManager(new HttpRequestException()));
        var validator = new SupabaseAccessTokenValidator(options);

        var result = await validator.ValidateAsync(Token(), CancellationToken.None);

        Assert.Equal(SupabaseAccessTokenValidationState.Unavailable, result.State);
        Assert.Null(result.Principal);
    }

    [Fact]
    public async Task ValidateAsync_propagates_configuration_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new OperationCanceledException(cancellation.Token);
        var validator = new SupabaseAccessTokenValidator(
            Options(new ThrowingConfigurationManager(expected)));

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            validator.ValidateAsync(Token(), cancellation.Token));

        Assert.Equal(cancellation.Token, actual.CancellationToken);
    }

    private static JwtBearerOptions Options(
        IConfigurationManager<OpenIdConnectConfiguration>? configurationManager = null)
    {
        return new JwtBearerOptions
        {
            MapInboundClaims = false,
            ConfigurationManager = configurationManager ?? new StaticConfigurationManager(SigningKey),
            TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(SigningKey),
                ClockSkew = TimeSpan.FromMinutes(1)
            }
        };
    }

    private static string Token(
        string issuer = Issuer,
        string audience = Audience,
        DateTime? expires = null,
        byte[]? SigningKey = null)
    {
        var now = DateTime.UtcNow;
        var tokenExpires = expires ?? now.AddHours(1);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, "user-123"),
                new Claim("email", "alice@example.test")
            ]),
            NotBefore = expires.HasValue ? tokenExpires.AddMinutes(-10) : now.AddMinutes(-5),
            Expires = tokenExpires,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(SigningKey ?? SupabaseAccessTokenValidatorTests.SigningKey),
                SecurityAlgorithms.HmacSha256)
        };

        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    private sealed class StaticConfigurationManager(byte[] signingKey)
        : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly OpenIdConnectConfiguration configuration = CreateConfiguration(signingKey);

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            Task.FromResult(configuration);

        public void RequestRefresh()
        {
        }

        private static OpenIdConnectConfiguration CreateConfiguration(byte[] signingKey)
        {
            var configuration = new OpenIdConnectConfiguration();
            configuration.SigningKeys.Add(new SymmetricSecurityKey(signingKey));
            return configuration;
        }
    }

    private sealed class ThrowingConfigurationManager(Exception exception)
        : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            Task.FromException<OpenIdConnectConfiguration>(exception);

        public void RequestRefresh()
        {
        }
    }
}
