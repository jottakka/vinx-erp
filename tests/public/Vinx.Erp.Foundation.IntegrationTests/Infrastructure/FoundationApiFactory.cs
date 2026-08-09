using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
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
    private readonly bool _useJwtBearer;

    private FoundationApiFactory(
        Guid actorUserId,
        Guid correlationId,
        TestPrincipalClaims principalClaims,
        JwtTestAuthority? jwtAuthority = null)
    {
        ActorUserId = actorUserId;
        CorrelationId = correlationId;
        _principalClaims = principalClaims;
        _jwtAuthority = jwtAuthority;
        _useJwtBearer = jwtAuthority is not null;
    }

    public Guid ActorUserId { get; }
    public Guid CorrelationId { get; }
    public FoundationPostgresFixture Fixture => _fixture;

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
            new TestPrincipalClaims(
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
            new TestPrincipalClaims(issuer, subject, email, correlationIdClaim));
        await factory._fixture.StartAsync();
        await factory._fixture.MigrateAsync();
        return factory;
    }

    public static async Task<FoundationApiFactory> CreateWithJwtBearerAsync()
    {
        var factory = new FoundationApiFactory(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new TestPrincipalClaims(null, null, null, null),
            new JwtTestAuthority());
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
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.MetadataAddress = JwtTestAuthority.MetadataAddress;
                    options.Backchannel = new HttpClient(new JwtMetadataHandler(_jwtAuthority!));
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        options.MetadataAddress,
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever(options.Backchannel));
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
}

public enum JwtTokenVariation
{
    Valid,
    InvalidSignature,
    InvalidIssuer,
    InvalidAudience,
    Expired
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
        var now = DateTime.UtcNow;
        var signingKey = new RsaSecurityKey(
            variation == JwtTokenVariation.InvalidSignature ? _invalidSigningRsa : _signingRsa)
        {
            KeyId = "test-key"
        };
        var token = new JwtSecurityToken(
            issuer: variation == JwtTokenVariation.InvalidIssuer ? "https://other.supabase.co/auth/v1" : Issuer,
            audience: variation == JwtTokenVariation.InvalidAudience ? "other-audience" : Audience,
            claims: [new Claim("sub", "external-subject")],
            notBefore: variation == JwtTokenVariation.Expired ? now.AddHours(-2) : now.AddMinutes(-1),
            expires: variation == JwtTokenVariation.Expired ? now.AddHours(-1) : now.AddMinutes(10),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

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
    string? CorrelationId);

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

        var identity = new ClaimsIdentity(identityClaims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
