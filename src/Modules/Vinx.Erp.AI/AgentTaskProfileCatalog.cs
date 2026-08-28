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

    public const string PaguaiFromDocumentProfileId = "paguai_from_document_v1";

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
    public static AgentTaskProfile CreateApprovedProfile(
        AgentModelSelection? modelSelection,
        int maxInputCharacters = AgentInputLimits.MainTurnMaxCharacters) =>
        CreateApprovedProfile(ReadOnlySummarizeProfileId, modelSelection, maxInputCharacters)
        ?? throw new InvalidOperationException("The read-only agent profile must be registered.");

    /// <summary>
    /// Cria o perfil aprovado identificado no catálogo com a seleção de
    /// provider decidida no servidor. Identificador desconhecido não resolve.
    /// </summary>
    public static AgentTaskProfile? CreateApprovedProfile(
        string profileId,
        AgentModelSelection? modelSelection,
        int maxInputCharacters = AgentInputLimits.MainTurnMaxCharacters)
    {
        if (!TryGet(profileId, out var catalogProfile))
        {
            return null;
        }

        var approved = catalogProfile! with
        {
            ModelSelection = modelSelection,
            MaxInputCharacters = maxInputCharacters,
        };
        approved.Validate();
        return approved;
    }

    private static Dictionary<string, AgentTaskProfile> CreateProfiles() =>
        new Dictionary<string, AgentTaskProfile>
        {
            [ReadOnlySummarizeProfileId] = CreateReadOnlySummarize(
                modelSelection: null,
                AgentInputLimits.MainTurnMaxCharacters),
            [PaguaiFromDocumentProfileId] = CreatePaguaiFromDocument(
                modelSelection: null,
                AgentInputLimits.MainTurnMaxCharacters),
        };

    private static AgentTaskProfile CreateReadOnlySummarize(
        AgentModelSelection? modelSelection,
        int maxInputCharacters) =>
        AgentTaskProfile.CreateValidated(
            ReadOnlySummarizeProfileId,
            "summarize",
            AgentJourneyScope.Company,
            AgentCapabilities.Read,
            modelSelection,
            timeout: TimeSpan.FromSeconds(30),
            turnLimit: 1,
            maxTotalTokens: 8_000,
            allowedTools: [],
            allowDocumentAttachments: true,
            maxInputCharacters);

    private static AgentTaskProfile CreatePaguaiFromDocument(
        AgentModelSelection? modelSelection,
        int maxInputCharacters) =>
        AgentTaskProfile.CreateValidated(
            PaguaiFromDocumentProfileId,
            PaguaiFromDocumentProfileId,
            AgentJourneyScope.Company,
            AgentCapabilities.Read,
            modelSelection,
            timeout: TimeSpan.FromSeconds(30),
            turnLimit: 1,
            maxTotalTokens: 8_000,
            allowedTools: [],
            allowDocumentAttachments: true,
            maxInputCharacters);
}
