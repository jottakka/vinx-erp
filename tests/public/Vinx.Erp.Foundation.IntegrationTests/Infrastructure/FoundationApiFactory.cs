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

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

public sealed class FoundationApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    public const string TestAuthenticationScheme = "FoundationTest";

    private readonly FoundationPostgresFixture _fixture = new();

    private FoundationApiFactory(Guid actorUserId, Guid correlationId)
    {
        ActorUserId = actorUserId;
        CorrelationId = correlationId;
    }

    public Guid ActorUserId { get; }
    public Guid CorrelationId { get; }
    public FoundationPostgresFixture Fixture => _fixture;

    public static async Task<FoundationApiFactory> CreateAsync(
        Guid? actorUserId = null,
        Guid? correlationId = null)
    {
        var factory = new FoundationApiFactory(actorUserId ?? Guid.NewGuid(), correlationId ?? Guid.NewGuid());
        await factory._fixture.StartAsync();
        await factory._fixture.MigrateAsync();
        return factory;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("FOUNDATION_RUNTIME_CONNECTION", _fixture.RuntimeConnectionString);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<FoundationDbContext>>();
            services.AddDbContext<FoundationDbContext>((_, options) => options
                .UseNpgsql(_fixture.RuntimeConnectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(new RowVersionInterceptor()));
            services.AddSingleton(new TestActorContext(ActorUserId, CorrelationId));
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
}

public sealed record TestActorContext(Guid UserId, Guid CorrelationId);

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TestActorContext actor) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actor.UserId.ToString()),
            new Claim("correlation_id", actor.CorrelationId.ToString())
        ],
        Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
