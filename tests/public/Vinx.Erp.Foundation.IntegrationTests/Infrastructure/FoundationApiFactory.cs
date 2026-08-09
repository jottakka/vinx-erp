using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

public sealed class FoundationApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    public const string TestAuthenticationScheme = "FoundationTest";

    private readonly FoundationPostgresFixture _fixture = new();
    private readonly TestPrincipalClaims _principalClaims;

    private FoundationApiFactory(Guid actorUserId, Guid correlationId, TestPrincipalClaims principalClaims)
    {
        ActorUserId = actorUserId;
        CorrelationId = correlationId;
        _principalClaims = principalClaims;
    }

    public Guid ActorUserId { get; }
    public Guid CorrelationId { get; }
    public FoundationPostgresFixture Fixture => _fixture;

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
            services.AddSingleton(_principalClaims);
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthenticationScheme;
                options.DefaultChallengeScheme = TestAuthenticationScheme;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationScheme,
                _ => { });
        });
    }

    public override async ValueTask DisposeAsync()
    {
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
