using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Vinx.Erp.Foundation.Identity;
using JsonClaimValueTypes = Microsoft.IdentityModel.JsonWebTokens.JsonClaimValueTypes;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

public sealed class FoundationApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    public const string TestAuthenticationScheme = "FoundationTest";

    private readonly FoundationPostgresFixture _fixture = new();
    private readonly TestPrincipalClaims _principalClaims;
    private readonly JwtTestAuthority? _jwtAuthority;
    private readonly CountingIdentityProvider? _countingIdentityProvider;
    private readonly bool _useJwtBearer;

    private FoundationApiFactory(
        Guid actorUserId,
        Guid correlationId,
        TestPrincipalClaims principalClaims,
        JwtTestAuthority? jwtAuthority = null,
        CountingIdentityProvider? countingIdentityProvider = null)
    {
        ActorUserId = actorUserId;
        CorrelationId = correlationId;
        _principalClaims = principalClaims;
        _jwtAuthority = jwtAuthority;
        _countingIdentityProvider = countingIdentityProvider;
        _useJwtBearer = jwtAuthority is not null;
    }

    public Guid ActorUserId { get; }
    public Guid CorrelationId { get; }
    public FoundationPostgresFixture Fixture => _fixture;
    public int IdentityProviderCallCount => _countingIdentityProvider?.CallCount
        ?? throw new InvalidOperationException("The factory is not configured with a counting identity provider.");
    public ClaimsPrincipal? AuthenticatedJwtPrincipal { get; private set; }

    public string CreateJwtToken(JwtTokenVariation variation) =>
        (_jwtAuthority ?? throw new InvalidOperationException("The factory is not configured for JWT bearer testing."))
        .CreateToken(variation);

    public static async Task<FoundationApiFactory> CreateAsync(
        Guid? actorUserId = null,
        Guid? correlationId = null)
    {
        var resolvedActorUserId = actorUserId ?? Guid.NewGuid();
        var resolvedCorrelationId = correlationId ?? Guid.NewGuid();
        var factory = new FoundationApiFactory(
            resolvedActorUserId,
            resolvedCorrelationId,
            CreateTestPrincipalClaims(
                "https://project.supabase.co/auth/v1",
                "external-subject",
                "same-address@example.test",
                resolvedCorrelationId.ToString()));
        await factory._fixture.StartAsync();
        await factory._fixture.MigrateAsync();
        return factory;
    }

    public static async Task<FoundationApiFactory> CreateWithAuthenticatedIdentityAsync(
        Guid? actorUserId = null,
        Guid? correlationId = null)
    {
        var factory = await CreateAsync(actorUserId, correlationId);
        await factory.SeedAuthenticatedActorAsync();
        return factory;
    }

    public static async Task<FoundationApiFactory> CreateWithClaimsAsync(
        string? issuer,
        string? subject,
        string? email = "same-address@example.test",
        string? correlationIdClaim = null)
    {
        var factory = new FoundationApiFactory(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CreateTestPrincipalClaims(issuer, subject, email, correlationIdClaim));
        await factory._fixture.StartAsync();
        await factory._fixture.MigrateAsync();
        return factory;
    }

    public static async Task<FoundationApiFactory> CreateWithJwtBearerAsync()
    {
        return await CreateWithJwtBearerAsync(useCountingIdentityProvider: false);
    }

    public static async Task<FoundationApiFactory> CreateWithJwtBearerAndCountingIdentityProviderAsync()
    {
        return await CreateWithJwtBearerAsync(useCountingIdentityProvider: true);
    }

    private static async Task<FoundationApiFactory> CreateWithJwtBearerAsync(bool useCountingIdentityProvider)
    {
        var identityProvider = useCountingIdentityProvider
            ? new CountingIdentityProvider(new SupabaseIdentityProvider(JwtTestAuthority.Issuer))
            : null;
        var factory = new FoundationApiFactory(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CreateTestPrincipalClaims(null, null, null, null),
            new JwtTestAuthority(),
            identityProvider);
        await factory._fixture.StartAsync();
        await factory._fixture.MigrateAsync();
        await factory.SeedAuthenticatedActorAsync();
        return factory;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("FOUNDATION_RUNTIME_CONNECTION", _fixture.RuntimeConnectionString);
        builder.UseSetting("SUPABASE_STORAGE_URL", "http://127.0.0.1:54321");
        builder.UseSetting("SUPABASE_STORAGE_SERVICE_ROLE_KEY", "test-service-role-key");
        builder.UseSetting("SUPABASE_AUTH_ISSUER", "https://project.supabase.co/auth/v1");
        builder.UseSetting("SUPABASE_AUTH_AUDIENCE", "authenticated");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<FoundationDbContext>>();
            services.AddDbContext<FoundationDbContext>((_, options) => options
                .UseNpgsql(_fixture.RuntimeConnectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(new RowVersionInterceptor()));
            if (_useJwtBearer)
            {
                if (_countingIdentityProvider is not null)
                {
                    services.RemoveAll<IIdentityProvider>();
                    services.AddSingleton<IIdentityProvider>(_countingIdentityProvider);
                }

                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.MetadataAddress = JwtTestAuthority.MetadataAddress;
                    options.Backchannel = new HttpClient(new JwtMetadataHandler(_jwtAuthority!));
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        options.MetadataAddress,
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever(options.Backchannel));
                    options.Events = new JwtBearerEvents
                    {
                        OnTokenValidated = context =>
                        {
                            AuthenticatedJwtPrincipal = context.Principal;
                            return Task.CompletedTask;
                        }
                    };
                });
            }
            else
            {
                services.AddSingleton(_principalClaims);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationScheme;
                    options.DefaultChallengeScheme = TestAuthenticationScheme;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationScheme,
                    _ => { });
            }
        });
    }

    public override async ValueTask DisposeAsync()
    {
        _jwtAuthority?.Dispose();
        await _fixture.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task SeedAuthenticatedActorAsync()
    {
        await using var administrator = _fixture.CreateAdministratorDbContext();
        administrator.AddRange(
            new User(ActorUserId, "Ana", "same-address@example.test"),
            new ExternalIdentity(
                Guid.NewGuid(),
                ActorUserId,
                "https://project.supabase.co/auth/v1",
                "external-subject"));
        await administrator.SaveChangesAsync();
    }

    private static TestPrincipalClaims CreateTestPrincipalClaims(
        string? issuer,
        string? subject,
        string? email,
        string? correlationId)
    {
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new TestPrincipalClaims(
            issuer,
            subject,
            email,
            correlationId,
            Guid.NewGuid().ToString("D"),
            issuedAt,
            issuedAt + 1,
            [
                new AmrEntry("password", issuedAt - 10),
                new AmrEntry("email", issuedAt - 20),
                new AmrEntry("token_refresh", issuedAt - 5)
            ]);
    }
}

public enum JwtTokenVariation
{
    Valid,
    InvalidSignature,
    InvalidIssuer,
    InvalidAudience,
    Expired,
    MissingSessionId,
    MalformedSessionId,
    MissingAmr,
    EmptyAmr,
    RefreshOnlyAmr,
    AuthenticatorAfterIssuedAt,
    IssuedAtAtOrAfterExpiresAt,
    TwoAmrEntries
}

internal sealed class JwtTestAuthority : IDisposable
{
    public const string Issuer = "https://project.supabase.co/auth/v1";
    public const string Audience = "authenticated";
    public const string MetadataAddress = "https://identity.test/.well-known/openid-configuration";
    private const string JwksAddress = "https://identity.test/keys";
    private readonly RSA _signingRsa = RSA.Create(2048);
    private readonly RSA _invalidSigningRsa = RSA.Create(2048);

    public string CreateToken(JwtTokenVariation variation)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = variation switch
        {
            JwtTokenVariation.Expired => now.ToUnixTimeSeconds() - 3600,
            JwtTokenVariation.IssuedAtAtOrAfterExpiresAt => now.ToUnixTimeSeconds() + 3600,
            _ => now.ToUnixTimeSeconds() + 1
        };
        var issuedAt = variation == JwtTokenVariation.IssuedAtAtOrAfterExpiresAt
            ? expiresAt
            : now.ToUnixTimeSeconds();
        var signingKey = new RsaSecurityKey(
            variation == JwtTokenVariation.InvalidSignature ? _invalidSigningRsa : _signingRsa)
        {
            KeyId = "test-key"
        };
        var claims = new List<Claim>
        {
            new("sub", "external-subject"),
            new("iat", issuedAt.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        };
        if (variation != JwtTokenVariation.MissingSessionId)
        {
            claims.Add(new Claim(
                "session_id",
                variation == JwtTokenVariation.MalformedSessionId ? "not-a-uuid" : Guid.NewGuid().ToString("D")));
        }

        claims.AddRange(CreateAmrClaims(variation, issuedAt));
        var token = new JwtSecurityToken(
            issuer: variation == JwtTokenVariation.InvalidIssuer ? "https://other.supabase.co/auth/v1" : Issuer,
            audience: variation == JwtTokenVariation.InvalidAudience ? "other-audience" : Audience,
            claims: claims,
            notBefore: variation == JwtTokenVariation.Expired ? now.AddHours(-2).UtcDateTime : now.AddMinutes(-1).UtcDateTime,
            expires: DateTimeOffset.FromUnixTimeSeconds(expiresAt).UtcDateTime,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static IEnumerable<Claim> CreateAmrClaims(JwtTokenVariation variation, long issuedAt)
    {
        if (variation == JwtTokenVariation.MissingAmr)
        {
            yield break;
        }

        if (variation == JwtTokenVariation.EmptyAmr)
        {
            yield return new Claim("amr", "[]", JsonClaimValueTypes.Json);
            yield break;
        }

        AmrEntry[] entries = variation switch
        {
            JwtTokenVariation.RefreshOnlyAmr =>
            [new AmrEntry("token_refresh", issuedAt - 1)],
            JwtTokenVariation.AuthenticatorAfterIssuedAt =>
            [new AmrEntry("password", issuedAt + 1)],
            JwtTokenVariation.TwoAmrEntries =>
            [
                new AmrEntry("password", issuedAt - 10),
                new AmrEntry("email", issuedAt - 20)
            ],
            _ =>
            [
                new AmrEntry("password", issuedAt - 10),
                new AmrEntry("email", issuedAt - 20),
                new AmrEntry("token_refresh", issuedAt - 5)
            ]
        };

        foreach (var entry in entries)
        {
            yield return new Claim("amr", SerializeAmrEntry(entry), JsonClaimValueTypes.Json);
        }
    }

    private static string SerializeAmrEntry(AmrEntry entry) => JsonSerializer.Serialize(new
    {
        method = entry.Method,
        timestamp = entry.Timestamp
    });

    public HttpResponseMessage GetResponse(Uri requestUri)
    {
        if (requestUri.AbsoluteUri == MetadataAddress)
        {
            return JsonResponse(new { issuer = Issuer, jwks_uri = JwksAddress });
        }

        if (requestUri.AbsoluteUri == JwksAddress)
        {
            var parameters = _signingRsa.ExportParameters(false);
            return JsonResponse(new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        kid = "test-key",
                        n = Base64UrlEncoder.Encode(parameters.Modulus),
                        e = Base64UrlEncoder.Encode(parameters.Exponent)
                    }
                }
            });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public void Dispose()
    {
        _signingRsa.Dispose();
        _invalidSigningRsa.Dispose();
    }

    private static HttpResponseMessage JsonResponse(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
    };
}

internal sealed class JwtMetadataHandler(JwtTestAuthority authority) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(authority.GetResponse(request.RequestUri
            ?? throw new InvalidOperationException("The JWT metadata request did not have a URI.")));
}

public sealed record TestPrincipalClaims(
    string? Issuer,
    string? Subject,
    string? Email,
    string? CorrelationId,
    string SessionId,
    long IssuedAt,
    long ExpiresAt,
    IReadOnlyList<AmrEntry> Amr);

public sealed record AmrEntry(string Method, long Timestamp);

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TestPrincipalClaims claims) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identityClaims = new List<Claim>();
        if (claims.Issuer is not null)
        {
            identityClaims.Add(new Claim("iss", claims.Issuer));
        }

        if (claims.Subject is not null)
        {
            identityClaims.Add(new Claim("sub", claims.Subject));
        }

        if (claims.Email is not null)
        {
            identityClaims.Add(new Claim(ClaimTypes.Email, claims.Email));
        }

        if (claims.CorrelationId is not null)
        {
            identityClaims.Add(new Claim("correlation_id", claims.CorrelationId));
        }

        identityClaims.Add(new Claim("session_id", claims.SessionId));
        identityClaims.Add(new Claim("iat", claims.IssuedAt.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        identityClaims.Add(new Claim("exp", claims.ExpiresAt.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        identityClaims.AddRange(claims.Amr.Select(entry =>
            new Claim("amr", JsonSerializer.Serialize(new
            {
                method = entry.Method,
                timestamp = entry.Timestamp
            }), JsonClaimValueTypes.Json)));

        var identity = new ClaimsIdentity(identityClaims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal sealed class CountingIdentityProvider(IIdentityProvider inner) : IIdentityProvider
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public bool TryGetExternalIdentityProof(ClaimsPrincipal principal, out ExternalIdentityProof proof)
    {
        Interlocked.Increment(ref _callCount);
        return inner.TryGetExternalIdentityProof(principal, out proof);
    }
}
