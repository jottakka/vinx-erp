namespace Vinx.Erp.AI;

/// <summary>
/// Catálogo server-side de perfis aprovados: única fonte de perfis para o
/// runner e a API. Perfis são registrados apenas aqui, via
/// <see cref="AgentTaskProfile.CreateValidated"/>; identificadores
/// desconhecidos, nulos ou em branco não resolvem (default deny).
/// </summary>
public sealed class AgentTaskProfileCatalog
{
    public const string ReadOnlySummarizeProfileId = "read_only_summarize_v1";

    private static readonly IReadOnlyDictionary<string, AgentTaskProfile> Profiles = CreateProfiles();

    private AgentTaskProfileCatalog()
    {
    }

    /// <summary>
    /// Retorna o perfil aprovado para o identificador; desconhecido, nulo ou em
    /// branco não resolvem (default deny).
    /// </summary>
    public static bool TryGet(string profileId, out AgentTaskProfile? profile)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            profile = null;
            return false;
        }

        return Profiles.TryGetValue(profileId, out profile);
    }

    /// <summary>
    /// Cria o perfil aprovado com a seleção de provider decidida no servidor:
    /// valores do catálogo + seleção informada. Seleção nula deixa o runner
    /// fail-closed em provider_not_configured (comportamento atual, #135).
    /// </summary>
    public static AgentTaskProfile CreateApprovedProfile(AgentModelSelection? modelSelection) =>
        CreateReadOnlySummarize(modelSelection);

    private static Dictionary<string, AgentTaskProfile> CreateProfiles() =>
        new Dictionary<string, AgentTaskProfile>
        {
            [ReadOnlySummarizeProfileId] = CreateReadOnlySummarize(modelSelection: null),
        };

    private static AgentTaskProfile CreateReadOnlySummarize(AgentModelSelection? modelSelection) =>
        AgentTaskProfile.CreateValidated(
            ReadOnlySummarizeProfileId,
            "summarize",
            AgentJourneyScope.Company,
            AgentCapabilities.Read,
            modelSelection,
            timeout: TimeSpan.FromSeconds(30),
            turnLimit: 1,
            maxTotalTokens: 8_000,
            allowedTools: []);
}
