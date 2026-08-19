namespace Vinx.Erp.ApiHost.Agent;

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

    /// <summary>Provider oficial DeepSeek, endpoint aprovado fixo.</summary>
    public const string DeepSeekProvider = "deepseek";

    /// <summary>Provider aprovado; nulo deixa o runner fail-closed.</summary>
    public string? Provider { get; set; }

    /// <summary>Endpoint absoluto do provider; obrigatório quando há provider.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Chave do provider (somente ambiente); nunca logada nem ecoada.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Modelo aprovado; obrigatório quando há provider.</summary>
    public string? Model { get; set; }

    /// <summary>Latência máxima aceita para a execução; deve ser positiva.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Hosts cujos links permanecem no texto redigido; demais viram [link-removido].</summary>
    public IReadOnlyList<string> AllowedLinkHosts { get; set; } = [];

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

        if (AllowedLinkHosts.Any(host => string.IsNullOrWhiteSpace(host)))
        {
            throw new InvalidOperationException("Agent allowed link hosts must be non-empty host names.");
        }

        if (string.IsNullOrWhiteSpace(Provider))
        {
            // Sem provider o runner falha fechado (provider_not_configured):
            // nenhuma outra configuração é exigida.
            return;
        }

        if (!string.Equals(Provider, OpenAiCompatibleProvider, StringComparison.Ordinal)
            && !string.Equals(Provider, DeepSeekProvider, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Agent provider must be '{OpenAiCompatibleProvider}' or '{DeepSeekProvider}'.");
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
    }
}
