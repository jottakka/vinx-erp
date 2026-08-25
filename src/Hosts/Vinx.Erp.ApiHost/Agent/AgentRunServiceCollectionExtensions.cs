using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vinx.Erp.AI;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.AI.Guardrails;
using Vinx.Erp.AI.Providers;
using Vinx.Erp.AI.Providers.DeepSeek;
using Vinx.Erp.AI.Providers.OpenAiCompatible;

namespace Vinx.Erp.ApiHost.Agent;

/// <summary>
/// Composição server-side da superfície de execução do agente: options
/// validadas on-start, gates determinísticos e a seleção de provider decidida
/// SOMENTE aqui — o request nunca escolhe endpoint, modelo ou chave. Sem
/// provider configurado nenhum model é registrado e o runner falha fechado
/// com provider_not_configured. A chave vem exclusivamente do cofre de
/// configuração e nunca é logada, ecoada em validação ou devolvida.
/// </summary>
internal static class AgentRunServiceCollectionExtensions
{
    private const string ConfigurationSection = "AgentRun";
    private const string ProviderHttpClientName = "agent-provider";
    private const string VisionHttpClientName = "agent-document-vision";

    /// <summary>Registra options, gates, provider client, runner e o serviço de aplicação de POST /agent/runs.</summary>
    public static IServiceCollection AddAgentRunSurface(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Snapshot da composição: a presença do provider decide o registro na
        // partida; configuração inválida derruba o host aqui mesmo, com a
        // mensagem segura da própria options.
        var agentRun =
            configuration.GetSection(ConfigurationSection).Get<AgentRunOptions>() ?? new AgentRunOptions();
        if (!IsValidConfiguration(agentRun))
        {
            throw new InvalidOperationException("Agent run configuration is invalid.");
        }

        services
            .AddOptions<AgentRunOptions>()
            .Bind(configuration.GetSection(ConfigurationSection))
            .Validate(IsValidConfiguration, "Agent run configuration is invalid.")
            .ValidateOnStart();

        services.AddScoped<IAgentContextResolver, AgentContextResolver>();
        services.AddSingleton<SourceAuthorizationGate>();
        services.AddSingleton<AgentOutputGate>();

        AgentModelSelection? selection = null;
        if (!string.IsNullOrWhiteSpace(agentRun.Provider))
        {
            selection = new AgentModelSelection(agentRun.Provider, agentRun.Model!);
            services.AddHttpClient(
                ProviderHttpClientName,
                client =>
                {
                    client.BaseAddress = agentRun.Endpoint;
                    client.Timeout = agentRun.Timeout;
                }
            );
            // A chave trafega somente da configuração para a credencial do
            // client aprovado; nenhum header, log ou mensagem a ecoa.
            services.AddScoped<IAgentModel>(serviceProvider => new ChatClientAgentModel(
                CreateProviderChatClient(
                    agentRun,
                    serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(ProviderHttpClientName),
                    serviceProvider.GetService<ILoggerFactory>())));
        }

        if (agentRun.Vision is { } vision)
        {
            var visionProfile = vision.ToProviderOptions();
            var allowedClassifications = vision.ToClassificationAllowlist();
            services.AddHttpClient(
                VisionHttpClientName,
                client =>
                {
                    client.BaseAddress = visionProfile.Endpoint;
                    client.Timeout = visionProfile.Timeout;
                });
            services.AddScoped<IAuthorizedDocumentSource, FoundationAuthorizedDocumentSource>();
            services.AddScoped(serviceProvider =>
            {
                var visionClient = new OpenAiCompatibleChatClient(
                    visionProfile,
                    serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(VisionHttpClientName),
                    serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<OpenAiCompatibleChatClient>());
                return new DocumentContextNormalizer(
                    serviceProvider.GetRequiredService<IAuthorizedDocumentSource>(),
                    new DocumentVisionOcr(visionClient),
                    DocumentContextLimits.FromVisionProfile(visionProfile, allowedClassifications));
            });
        }

        services.AddScoped(serviceProvider => new AgentRunner(
            serviceProvider.GetRequiredService<IAgentContextResolver>(),
            // Sem provider registrado o model é null: o runner falha fechado.
            serviceProvider.GetService<IAgentModel>(),
            serviceProvider.GetRequiredService<ILogger<AgentRunner>>(),
            profileCatalog: CreateProfileCatalog(selection, AgentInputLimits.MainTurnMaxCharacters),
            outputGate: serviceProvider.GetRequiredService<AgentOutputGate>(),
            documentContextNormalizer: serviceProvider.GetService<DocumentContextNormalizer>()));
        services.AddScoped<AgentExecutionApplicationService>();
        return services;
    }

    // Sem provider nenhuma outra configuração é exigida (fail-closed em
    // provider_not_configured); com provider, endpoint/model aprovados e chave
    // no cofre são obrigatórios. A validação delega à options da fatia do núcleo.
    private static bool IsValidConfiguration(AgentRunOptions options)
    {
        try
        {
            options.Validate();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(options.Provider) || !string.IsNullOrWhiteSpace(options.ApiKey);
    }

    private static IChatClient CreateProviderChatClient(
        AgentRunOptions options,
        HttpClient httpClient,
        ILoggerFactory? loggerFactory)
    {
        // Defesa em profundidade: a composição só chega aqui com provider
        // validado; endpoint e modelo ausentes rejeitam na construção.
        var endpoint =
            options.Endpoint ?? throw new InvalidOperationException("Agent endpoint must be an absolute URI.");
        var model =
            string.IsNullOrWhiteSpace(options.Model)
                ? throw new InvalidOperationException("Agent model must be a non-empty approved model name.")
                : options.Model;

        return string.Equals(options.Provider, AgentRunOptions.DeepSeekProvider, StringComparison.Ordinal)
            ? new DeepSeekChatClient(
                new DeepSeekOptions
                {
                    Endpoint = endpoint,
                    ApiKey = options.ApiKey ?? string.Empty,
                    Model = model,
                    Timeout = options.Timeout,
                    MaxInputCharacters = AgentInputLimits.MainTurnMaxCharacters,
                },
                httpClient,
                loggerFactory?.CreateLogger<DeepSeekChatClient>())
            : new OpenAiCompatibleChatClient(
                new OpenAiCompatibleOptions
                {
                    Endpoint = endpoint,
                    ApiKey = options.ApiKey ?? string.Empty,
                    Model = model,
                    Timeout = options.Timeout,
                    MaxInputCharacters = AgentInputLimits.MainTurnMaxCharacters,
                },
                httpClient,
                loggerFactory?.CreateLogger<OpenAiCompatibleChatClient>());
    }

    // Catálogo fixo: perfis aprovados do catálogo central com a seleção de
    // provider decidida na composição; sem provider a seleção permanece nula e
    // o runner falha fechado antes de qualquer chamada externa.
    private static Func<string, AgentTaskProfile?> CreateProfileCatalog(
        AgentModelSelection? selection,
        int maxInputCharacters) =>
        profileId =>
            AgentTaskProfileCatalog.TryGet(profileId, out _)
                ? AgentTaskProfileCatalog.CreateApprovedProfile(selection, maxInputCharacters)
                : null;
}
