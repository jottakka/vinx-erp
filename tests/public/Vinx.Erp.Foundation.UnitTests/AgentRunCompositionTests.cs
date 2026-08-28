using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vinx.Erp.AI;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.AI.Guardrails;
using Vinx.Erp.AI.Providers;
using Vinx.Erp.ApiHost.Agent;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Paguai;
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
            ("AgentRun:Endpoint", "https://API.EXAMPLE.COM/v1/"),
            ("AgentRun:Model", "approved-model"),
            ("AgentRun:ApprovedHosts:0", " api.example.com. "));

        var services = CreateBaseServices();
        services.AddAgentRunSurface(configuration, ReadDedicatedCredential);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<ChatClientAgentModel>(provider.GetRequiredService<IAgentModel>());
        Assert.NotNull(provider.GetRequiredService<AgentRunner>());
        Assert.NotNull(provider.GetRequiredService<AgentOutputGate>());
    }

    [Fact]
    public async Task Paguai_profile_uses_its_catalog_contract_with_the_server_selected_model()
    {
        var configuration = CreateConfiguration(
            ("AgentRun:Provider", AgentRunOptions.OpenAiCompatibleProvider),
            ("AgentRun:Endpoint", "https://api.example.com/v1/"),
            ("AgentRun:Model", "approved-model"),
            ("AgentRun:ApprovedHosts:0", "api.example.com"));
        var services = CreateBaseServices();
        services.AddAgentRunSurface(configuration, ReadDedicatedCredential);

        var companyId = Guid.NewGuid();
        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());
        var resolver = new CapturingContextResolver(new AgentExecutionContext(
            actor.UserId,
            actor.CorrelationId,
            companyId,
            "Empresa Aprovada",
            Guid.NewGuid(),
            AgentTaskProfileCatalog.PaguaiFromDocumentProfileId,
            new HashSet<string>()));
        services.AddScoped<IAgentContextResolver>(_ => resolver);

        using var provider = services.BuildServiceProvider();
        var outcome = await provider.GetRequiredService<AgentRunner>().RunAsync(
            actor,
            AgentTaskProfileCatalog.PaguaiFromDocumentProfileId,
            "extraia a fatura",
            companyId,
            [Guid.NewGuid()],
            CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.NeedsClarification, outcome.Status);
        Assert.Equal(AgentOutcomeReason.DocumentContextUnavailable, outcome.ReasonCode);
        var profile = Assert.IsType<AgentTaskProfile>(resolver.Profile);
        Assert.Equal(AgentTaskProfileCatalog.PaguaiFromDocumentProfileId, profile.ProfileId);
        Assert.Equal(AgentTaskProfileCatalog.PaguaiFromDocumentProfileId, profile.Journey);
        Assert.True(profile.AllowDocumentAttachments);
        Assert.Equal(new AgentModelSelection(AgentRunOptions.OpenAiCompatibleProvider, "approved-model"), profile.ModelSelection);
    }

    [Fact]
    public void Text_runtime_profile_rejects_a_parent_allowlist_entry()
    {
        var configuration = CreateConfiguration(
            ("AgentRun:Provider", AgentRunOptions.OpenAiCompatibleProvider),
            ("AgentRun:Endpoint", "https://api.opencode.ai/zen/go/v1/"),
            ("AgentRun:Model", "approved-model"),
            ("AgentRun:ApprovedHosts:0", "opencode.ai"));

        Assert.Throws<InvalidOperationException>(
            () => CreateBaseServices().AddAgentRunSurface(configuration, ReadDedicatedCredential));
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
    public void Provider_without_the_dedicated_process_key_fails_the_composition()
    {
        var configuration = CreateConfiguration(
            ("AgentRun:Provider", AgentRunOptions.OpenAiCompatibleProvider),
            ("AgentRun:Endpoint", "https://api.example.com/v1/"),
            ("AgentRun:Model", "approved-model"),
            ("AgentRun:ApiKey", "ignored-test-key"));

        Assert.Throws<InvalidOperationException>(
            () => CreateBaseServices().AddAgentRunSurface(configuration, _ => null));
    }

    [Fact]
    public void Loopback_http_endpoint_fails_the_composition()
    {
        // A política de egress do módulo rejeita loopback: a options espelha a
        // regra e derruba o host na partida em vez de falhar opaco por request.
        var configuration = CreateConfiguration(
            ("AgentRun:Provider", AgentRunOptions.OpenAiCompatibleProvider),
            ("AgentRun:Endpoint", "http://127.0.0.1:9999"),
            ("AgentRun:Model", "approved-model"));

        Assert.Throws<InvalidOperationException>(
            () => CreateBaseServices().AddAgentRunSurface(configuration));
    }

    [Fact]
    public void Https_loopback_endpoint_fails_the_composition()
    {
        var configuration = CreateConfiguration(
            ("AgentRun:Provider", AgentRunOptions.OpenAiCompatibleProvider),
            ("AgentRun:Endpoint", "https://127.0.0.1/v1/"),
            ("AgentRun:Model", "approved-model"),
            ("AgentRun:ApprovedHosts:0", "127.0.0.1"));

        Assert.Throws<InvalidOperationException>(
            () => CreateBaseServices().AddAgentRunSurface(configuration, ReadDedicatedCredential));
    }

    [Fact]
    public void Complete_document_vision_configuration_registers_the_normalizer_as_scoped()
    {
        var configuration = CreateConfiguration(
            ("AgentRun:Provider", AgentRunOptions.OpenAiCompatibleProvider),
            ("AgentRun:Endpoint", "https://VISION.EXAMPLE.COM/v1/"),
            ("AgentRun:Model", "approved-vision-model"),
            ("AgentRun:ApprovedHosts:0", " vision.example.com. "),
            ("AgentRun:Vision:Provider", AgentRunOptions.OpenAiCompatibleProvider),
            ("AgentRun:Vision:Endpoint", "https://VISION.EXAMPLE.COM/v1/"),
            ("AgentRun:Vision:Model", "approved-vision-model"),
            ("AgentRun:Vision:ApprovedHosts:0", " vision.example.com. "),
            ("AgentRun:Vision:Timeout", TimeSpan.FromSeconds(20).ToString()),
            ("AgentRun:Vision:AllowedClassifications:0", "fiscal"));
        var services = CreateBaseServices();

        services.AddAgentRunSurface(configuration, ReadDedicatedCredential);

        var normalizer = Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(DocumentContextNormalizer));
        Assert.Equal(ServiceLifetime.Scoped, normalizer.Lifetime);
        var source = Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IAuthorizedDocumentSource));
        Assert.Equal(ServiceLifetime.Scoped, source.Lifetime);
    }

    [Fact]
    public void Vision_runtime_profile_rejects_a_parent_allowlist_entry()
    {
        var configuration = CreateConfiguration(
            ("AgentRun:Provider", AgentRunOptions.OpenAiCompatibleProvider),
            ("AgentRun:Endpoint", "https://vision.opencode.ai/zen/go/v1/"),
            ("AgentRun:Model", "approved-vision-model"),
            ("AgentRun:ApprovedHosts:0", "vision.opencode.ai"),
            ("AgentRun:Vision:Provider", AgentRunOptions.OpenAiCompatibleProvider),
            ("AgentRun:Vision:Endpoint", "https://vision.opencode.ai/zen/go/v1/"),
            ("AgentRun:Vision:Model", "approved-vision-model"),
            ("AgentRun:Vision:ApprovedHosts:0", "opencode.ai"),
            ("AgentRun:Vision:AllowedClassifications:0", "fiscal"));

        Assert.Throws<InvalidOperationException>(
            () => CreateBaseServices().AddAgentRunSurface(configuration, ReadDedicatedCredential));
    }

    [Fact]
    public void Vision_requires_the_identical_main_profile()
    {
        AssertInvalid(CreateMatchingOptions(includeMain: false));

        var differentEndpoint = CreateMatchingOptions();
        differentEndpoint.Vision!.Endpoint = new Uri("https://other.example/v1/");
        differentEndpoint.Vision.ApprovedHosts = ["other.example"];
        AssertInvalid(differentEndpoint);

        var differentModel = CreateMatchingOptions();
        differentModel.Vision!.Model = "different-model";
        AssertInvalid(differentModel);

        var differentHosts = CreateMatchingOptions();
        differentHosts.Vision!.ApprovedHosts = ["api.example", "other.example"];
        AssertInvalid(differentHosts);

        var normalizedEquivalent = CreateMatchingOptions();
        normalizedEquivalent.Endpoint = new Uri("https://API.EXAMPLE/v1/");
        normalizedEquivalent.ApprovedHosts = [" api.example. ", "SECOND.EXAMPLE"];
        normalizedEquivalent.Timeout = TimeSpan.FromSeconds(12);
        normalizedEquivalent.Vision!.Endpoint = new Uri("https://api.example:443/v1/");
        normalizedEquivalent.Vision.ApprovedHosts = ["second.example.", "api.example"];
        normalizedEquivalent.Vision.Timeout = TimeSpan.FromSeconds(45);

        normalizedEquivalent.Validate();

        static void AssertInvalid(AgentRunOptions options) =>
            Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [MemberData(nameof(InvalidVisionConfigurations))]
    public void Partial_or_empty_document_vision_configuration_fails_with_a_fixed_message(
        (string Key, string? Value)[] values)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateBaseServices().AddAgentRunSurface(CreateConfiguration(values)));

        Assert.Equal("Agent run configuration is invalid.", exception.Message);
        Assert.DoesNotContain("vision.example.com", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-vision-api-key", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("approved-vision-model", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fiscal", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<(string Key, string? Value)[]> InvalidVisionConfigurations => new()
    {
        new[]
        {
            ("AgentRun:Vision:Provider", (string?)AgentRunOptions.OpenAiCompatibleProvider),
        },
        new[]
        {
            ("AgentRun:Vision:Provider", (string?)AgentRunOptions.OpenAiCompatibleProvider),
            ("AgentRun:Vision:Endpoint", "https://vision.example.com/v1/"),
            ("AgentRun:Vision:Model", "approved-vision-model"),
        },
        new[]
        {
            ("AgentRun:Vision:Provider", (string?)"unsupported"),
            ("AgentRun:Vision:Endpoint", "https://vision.example.com/v1/"),
            ("AgentRun:Vision:Model", "approved-vision-model"),
            ("AgentRun:Vision:AllowedClassifications:0", "fiscal"),
        },
    };

    // Mesma base do host: persistência, auditoria e resolvedor de empresas
    // pertencem à Foundation/Program.cs; a composição só agrega a sua superfície.
    private static ServiceCollection CreateBaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<FoundationDbContext>(options =>
            options.UseNpgsql("Host=localhost;Database=vinx;Username=postgres;Password=postgres"));
        services.AddScoped<AuditAppender>();
        services.AddScoped<CapabilityEvaluator>();
        services.AddScoped<PaguaiProposalService>();
        services.AddScoped<AuthorizedCompanySetResolver>();
        return services;
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                pairs.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    private static AgentRunOptions CreateMatchingOptions(bool includeMain = true) => new()
    {
        Provider = includeMain ? AgentRunOptions.OpenAiCompatibleProvider : null,
        Endpoint = includeMain ? new Uri("https://api.example/v1/") : null,
        Model = includeMain ? "approved-model" : null,
        ApprovedHosts = includeMain ? ["api.example"] : [],
        Vision = new AgentRunVisionOptions
        {
            Provider = AgentRunOptions.OpenAiCompatibleProvider,
            Endpoint = new Uri("https://api.example/v1/"),
            Model = "approved-model",
            ApprovedHosts = ["api.example"],
            AllowedClassifications = ["agentic_attachment"],
        },
    };

    private static string? ReadDedicatedCredential(string name) =>
        name == "VINX_AGENT_PROVIDER_API_KEY" ? "synthetic-test-key" : null;

    private sealed class StaticContextResolver(AgentContextResolution resolution) : IAgentContextResolver
    {
        public Task<AgentContextResolution> ResolveAsync(
            ActorContext actor,
            AgentTaskProfile profile,
            Guid? requestedCompanyId,
            CancellationToken cancellationToken) => Task.FromResult(resolution);
    }

    private sealed class CapturingContextResolver(AgentExecutionContext context) : IAgentContextResolver
    {
        public AgentTaskProfile? Profile { get; private set; }

        public Task<AgentContextResolution> ResolveAsync(
            ActorContext actor,
            AgentTaskProfile profile,
            Guid? requestedCompanyId,
            CancellationToken cancellationToken)
        {
            Profile = profile;
            return Task.FromResult(new AgentContextResolution(AgentContextResolutionStatus.Resolved, "resolved", context));
        }
    }
}
