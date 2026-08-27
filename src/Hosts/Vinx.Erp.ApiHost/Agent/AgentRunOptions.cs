namespace Vinx.Erp.ApiHost.Agent;

using Vinx.Erp.AI.Providers.OpenAiCompatible;

/// <summary>
/// Configuração aprovada da execução do agente, vinculada on-start pela
/// composição (Task 4). Sem provider a execução falha fechada com
/// provider_not_configured; a chave vem somente do ambiente e nunca é logada,
/// ecoada em validação ou devolvida em resposta.
/// </summary>
public sealed class AgentRunOptions
{
    /// <summary>Provider OpenAI-compatible genérico.</summary>
    public const string OpenAiCompatibleProvider = "openai_compatible";

    /// <summary>Provider aprovado; nulo deixa o runner fail-closed.</summary>
    public string? Provider { get; set; }

    /// <summary>Endpoint absoluto do provider; obrigatório quando há provider.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Modelo aprovado; obrigatório quando há provider.</summary>
    public string? Model { get; set; }

    /// <summary>Hosts exatos aprovados do perfil, definidos somente no servidor.</summary>
    public IReadOnlyList<string> ApprovedHosts { get; set; } = [];

    /// <summary>Latência máxima aceita para a execução; deve ser positiva.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Perfil opcional e exclusivamente server-side para OCR de anexos.</summary>
    public AgentRunVisionOptions? Vision { get; set; }

    /// <summary>
    /// Valida os invariantes; lança <see cref="InvalidOperationException"/> com
    /// mensagem segura que nunca ecoa endpoint, modelo e muito menos a chave.
    /// </summary>
    public void Validate()
    {
        if (Timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Agent timeout must be positive.");
        }

        Vision?.Validate();

        if (string.IsNullOrWhiteSpace(Provider))
        {
            if (Vision is not null)
            {
                throw new InvalidOperationException("Agent document vision requires the main provider profile.");
            }

            // Sem provider o runner falha fechado (provider_not_configured):
            // nenhuma outra configuração é exigida.
            return;
        }

        if (!string.Equals(Provider, OpenAiCompatibleProvider, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Agent provider must be '{OpenAiCompatibleProvider}'.");
        }

        if (Endpoint is null || !Endpoint.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Agent endpoint must be an absolute URI when a provider is configured.");
        }

        if (Endpoint.Scheme != Uri.UriSchemeHttps)
        {
            // Espelha a política de egress do módulo: loopback nunca é provider.
            throw new InvalidOperationException("Agent endpoint must use HTTPS.");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidOperationException(
                "Agent model must be a non-empty approved model name when a provider is configured.");
        }

        if (ApprovedHosts.Count == 0 || ApprovedHosts.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Agent provider must have an approved host list.");
        }

        if (!HasExactApprovedHost(Endpoint, ApprovedHosts))
        {
            throw new InvalidOperationException("Agent provider endpoint must match an approved host exactly.");
        }

        if (Vision is not null
            && (!string.Equals(Provider, Vision.Provider, StringComparison.Ordinal)
                || !string.Equals(Model, Vision.Model, StringComparison.Ordinal)
                || !ExactApprovedHostPolicy.ProfilesMatch(
                    Endpoint,
                    ApprovedHosts,
                    Vision.Endpoint,
                    Vision.ApprovedHosts)))
        {
            throw new InvalidOperationException("Agent document vision must use the main provider profile.");
        }
    }

    internal static bool HasExactApprovedHost(Uri? endpoint, IReadOnlyList<string> approvedHosts)
    {
        ArgumentNullException.ThrowIfNull(approvedHosts);
        return ExactApprovedHostPolicy.Matches(endpoint, approvedHosts);
    }
}

/// <summary>Configuração server-side do perfil OpenAI-compatible usado somente por OCR.</summary>
public sealed class AgentRunVisionOptions
{
    public string? Provider { get; set; }

    public Uri? Endpoint { get; set; }

    public string? Model { get; set; }

    public IReadOnlyList<string> ApprovedHosts { get; set; } = [];

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Única autoridade de consentimento para classificação de anexos.</summary>
    public IReadOnlyList<string> AllowedClassifications { get; set; } = [];

    internal void Validate()
    {
        if (!string.Equals(Provider, AgentRunOptions.OpenAiCompatibleProvider, StringComparison.Ordinal)
            || AllowedClassifications.Count == 0
            || AllowedClassifications.Any(string.IsNullOrWhiteSpace)
            || ApprovedHosts.Count == 0
            || ApprovedHosts.Any(string.IsNullOrWhiteSpace)
            || !AgentRunOptions.HasExactApprovedHost(Endpoint, ApprovedHosts)
            || new OpenAiCompatibleOptions.Validator().ValidateForRuntimeCredential(null, ToProviderOptions()).Failed)
        {
            throw new InvalidOperationException("Agent document vision configuration is invalid.");
        }
    }

    internal OpenAiCompatibleOptions ToProviderOptions() => new()
    {
        Endpoint = Endpoint,
        Model = Model ?? string.Empty,
        Timeout = Timeout,
        ApprovedHosts = ApprovedHosts,
        AllowImageInputs = true,
        ModuleTag = "ai/agent-document-vision",
    };

    internal IReadOnlySet<string> ToClassificationAllowlist() =>
        new HashSet<string>(AllowedClassifications, StringComparer.Ordinal);
}
