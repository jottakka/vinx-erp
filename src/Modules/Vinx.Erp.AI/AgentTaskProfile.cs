namespace Vinx.Erp.AI;

/// <summary>
/// Escopo do journey aprovado. <see cref="Company"/> é o único valor: não existe
/// execução de agente fora do contexto de empresa.
/// </summary>
public enum AgentJourneyScope
{
    Company,
}

/// <summary>
/// Perfil de tarefa aprovado no servidor: única fonte de journey, escopo,
/// capability, modelo, timeout, orçamento e ferramentas que o agente pode usar.
/// Propriedades init-only — clientes não injetam valores após a construção;
/// perfis válidos nascem de <see cref="CreateValidated"/> ou do catálogo.
/// TurnLimit é fixado em 1 nesta versão: exigir mais de 1 depende de revisão de
/// política quando houver loop aprovado. ModelSelection nula significa que o
/// provedor ainda não foi atribuído (#135); o runner falha fechado com
/// provider_not_configured.
/// </summary>
public sealed record AgentTaskProfile(
    string ProfileId,
    string Journey,
    AgentJourneyScope Scope,
    string RequiredCapabilityName,
    AgentModelSelection? ModelSelection,
    TimeSpan Timeout,
    int TurnLimit,
    int MaxTotalTokens,
    IReadOnlyList<string> AllowedTools)
{
    /// <summary>
    /// Constrói o perfil já validado; lança <see cref="ArgumentException"/>
    /// apontando o parâmetro inválido.
    /// </summary>
    public static AgentTaskProfile CreateValidated(
        string profileId,
        string journey,
        AgentJourneyScope scope,
        string requiredCapabilityName,
        AgentModelSelection? modelSelection,
        TimeSpan timeout,
        int turnLimit,
        int maxTotalTokens,
        IReadOnlyList<string> allowedTools)
    {
        var profile = new AgentTaskProfile(
            profileId,
            journey,
            scope,
            requiredCapabilityName,
            modelSelection,
            timeout,
            turnLimit,
            maxTotalTokens,
            allowedTools);
        profile.Validate();
        return profile;
    }

    /// <summary>
    /// Valida os invariantes do perfil; usado pelo catálogo antes de expor um
    /// perfil aprovado. Lança <see cref="ArgumentException"/> com o nome da
    /// propriedade inválida.
    /// </summary>
    public void Validate()
    {
        RejectNullOrWhiteSpace(ProfileId, nameof(ProfileId));
        RejectNullOrWhiteSpace(Journey, nameof(Journey));
        RejectNullOrWhiteSpace(RequiredCapabilityName, nameof(RequiredCapabilityName));

        if (!Enum.IsDefined(Scope))
        {
            throw new ArgumentException("The journey scope is not an approved value.", nameof(Scope));
        }

        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("The timeout must be positive.", nameof(Timeout));
        }

        if (TurnLimit != 1)
        {
            throw new ArgumentException(
                "The turn limit must be 1 in this version; a higher limit requires an approved loop policy.",
                nameof(TurnLimit));
        }

        if (MaxTotalTokens <= 0)
        {
            throw new ArgumentException("The total token budget must be positive.", nameof(MaxTotalTokens));
        }
    }

    private static void RejectNullOrWhiteSpace(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value must not be empty or whitespace.", paramName);
        }
    }
}
