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

    private static IReadOnlyDictionary<string, AgentTaskProfile> CreateProfiles()
    {
        var readOnlySummarize = AgentTaskProfile.CreateValidated(
            ReadOnlySummarizeProfileId,
            "summarize",
            AgentJourneyScope.Company,
            AgentCapabilities.Read,
            modelSelection: null,
            timeout: TimeSpan.FromSeconds(30),
            turnLimit: 1,
            maxTotalTokens: 8_000,
            allowedTools: Array.Empty<string>());

        return new Dictionary<string, AgentTaskProfile>
        {
            [readOnlySummarize.ProfileId] = readOnlySummarize,
        };
    }
}
