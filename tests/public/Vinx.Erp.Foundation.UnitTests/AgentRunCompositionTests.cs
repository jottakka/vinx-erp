using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vinx.Erp.AI;
using Vinx.Erp.AI.Guardrails;
using Vinx.Erp.AI.Providers;
using Vinx.Erp.ApiHost.Agent;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class AgentRunCompositionTests
{
    private const string ProfileId = AgentTaskProfileCatalog.ReadOnlySummarizeProfileId;

    [Fact]
    public async Task Without_provider_surface_resolves_and_runner_fails_closed()
    {
        var services = CreateBaseServices();
        services.AddAgentRunSurface(CreateConfiguration());
        var companyId = Guid.NewGuid();
        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());
        services.AddScoped<IAgentContextResolver>(_ => new StaticContextResolver(new AgentContextResolution(
            AgentContextResolutionStatus.Resolved,
            "resolved",
            new AgentExecutionContext(
                actor.UserId,
                actor.CorrelationId,
                companyId,
                "Empresa Aprovada",
                Guid.NewGuid(),
                ProfileId,
                new HashSet<string> { "summarize" }))));

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<IAgentModel>());
        Assert.NotNull(provider.GetRequiredService<AgentRunner>());
        Assert.NotNull(provider.GetRequiredService<AgentOutputGate>());
        Assert.NotNull(provider.GetRequiredService<AgentExecutionApplicationService>());

        var outcome = await provider
            .GetRequiredService<AgentRunner>()
            .RunAsync(actor, ProfileId, "Resuma o último pedido do cliente.", companyId, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Unavailable, outcome.Status);
        Assert.Equal(AgentOutcomeReason.ProviderNotConfigured, outcome.ReasonCode);
        var boundary = AgentExecutionApplicationService.MapOutcome(outcome);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, boundary.HttpStatusCode);
        Assert.Equal(AgentOutcomeReason.ProviderNotConfigured, boundary.ErrorCode);
    }

    [Fact]
    public void With_openai_compatible_provider_resolves_the_agent_model()
    {
        var configuration = CreateConfiguration(
            ("AgentRun:Provider", AgentRunOptions.OpenAiCompatibleProvider),
            ("AgentRun:Endpoint", "https://api.example.com/v1/"),
            ("AgentRun:Model", "approved-model"),
            ("AgentRun:ApiKey", "test-api-key"),
            ("AgentRun:AllowedLinkHosts:0", "docs.vinx.example"));

        var services = CreateBaseServices();
        services.AddAgentRunSurface(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<ChatClientAgentModel>(provider.GetRequiredService<IAgentModel>());
        Assert.NotNull(provider.GetRequiredService<AgentRunner>());
        Assert.NotNull(provider.GetRequiredService<AgentOutputGate>());
    }

    [Fact]
    public void Unknown_provider_fails_the_composition()
    {
        var configuration = CreateConfiguration(
            ("AgentRun:Provider", "not_a_provider"),
            ("AgentRun:Endpoint", "https://api.example.com/v1/"),
            ("AgentRun:Model", "approved-model"));

        Assert.Throws<InvalidOperationException>(
            () => CreateBaseServices().AddAgentRunSurface(configuration));
    }

    [Fact]
    public void Provider_without_api_key_fails_the_composition()
    {
        var configuration = CreateConfiguration(
            ("AgentRun:Provider", AgentRunOptions.OpenAiCompatibleProvider),
            ("AgentRun:Endpoint", "https://api.example.com/v1/"),
            ("AgentRun:Model", "approved-model"));

        Assert.Throws<InvalidOperationException>(
            () => CreateBaseServices().AddAgentRunSurface(configuration));
    }

    // Mesma base do host: persistência, auditoria e resolvedor de empresas
    // pertencem à Foundation/Program.cs; a composição só agrega a sua superfície.
    private static ServiceCollection CreateBaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<FoundationDbContext>(options =>
            options.UseNpgsql("Host=localhost;Database=vinx;Username=postgres;Password=postgres"));
        services.AddScoped<AuditAppender>();
        services.AddScoped<AuthorizedCompanySetResolver>();
        return services;
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                pairs.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    private sealed class StaticContextResolver(AgentContextResolution resolution) : IAgentContextResolver
    {
        public Task<AgentContextResolution> ResolveAsync(
            ActorContext actor,
            AgentTaskProfile profile,
            Guid? requestedCompanyId,
            CancellationToken cancellationToken) => Task.FromResult(resolution);
    }
}
