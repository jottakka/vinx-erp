namespace Vinx.Erp.AI;

/// <summary>Classificação de risco de uma ferramenta VINX.</summary>
public enum VinxToolRisk
{
    ReadOnly,
    Write,
}

/// <summary>
/// Allowlist, versão de política, capability e classificação de risco das
/// ferramentas do harness — todos definidos pelo servidor, nunca fornecidos
/// por cliente ou modelo. Nenhuma ferramenta fora da allowlist pode ser
/// aprovada; escritas exigem confirmação explícita do servidor.
/// </summary>
public static class VinxToolPolicy
{
    public const string ProbeStatusToolName = "GetHarnessProbeStatus";
    public const string ProbeWriteToolName = "RecordHarnessProbeWrite";

    /// <summary>
    /// Versão de política vigente, pertencente ao servidor. É gravada na
    /// preparação e revalidada na confirmação; divergência nega o consumo.
    /// </summary>
    public const string PolicyVersion = "2026-08-14.1";

    private static readonly IReadOnlyDictionary<string, VinxToolRisk> Allowlist =
        new Dictionary<string, VinxToolRisk>(StringComparer.Ordinal)
        {
            [ProbeStatusToolName] = VinxToolRisk.ReadOnly,
            [ProbeWriteToolName] = VinxToolRisk.Write,
        };

    public static IReadOnlyCollection<string> AllowedToolNames { get; } = Allowlist.Keys.ToArray();

    /// <summary>
    /// Capability exigida para executar uma ferramenta da allowlist. Leituras
    /// não exigem capability de escrita; escritas exigem <c>ai.writes.confirm</c>.
    /// Retorna <c>null</c> quando a ferramenta está fora da allowlist.
    /// </summary>
    public static string? CapabilityForTool(string? toolName)
    {
        if (!TryClassify(toolName, out var risk))
        {
            return null;
        }

        return risk == VinxToolRisk.Write
            ? Vinx.Erp.Foundation.Authorization.KnownCapabilities.AiWritesConfirm
            : null;
    }

    public static bool TryClassify(string? toolName, out VinxToolRisk risk)
    {
        if (toolName is not null && Allowlist.TryGetValue(toolName, out var classified))
        {
            risk = classified;
            return true;
        }

        risk = VinxToolRisk.Write;
        return false;
    }
}
