namespace Vinx.Erp.AI;

/// <summary>Classificação de risco de uma ferramenta VINX.</summary>
public enum VinxToolRisk
{
    ReadOnly,
    Write,
}

/// <summary>
/// Allowlist e classificação de risco das ferramentas do harness, definidas
/// pelo servidor. Nenhuma ferramenta fora da allowlist pode ser aprovada;
/// escritas exigem confirmação explícita.
/// </summary>
public static class VinxToolPolicy
{
    public const string ProbeStatusToolName = "GetHarnessProbeStatus";
    public const string ProbeWriteToolName = "RecordHarnessProbeWrite";

    private static readonly IReadOnlyDictionary<string, VinxToolRisk> Allowlist =
        new Dictionary<string, VinxToolRisk>(StringComparer.Ordinal)
        {
            [ProbeStatusToolName] = VinxToolRisk.ReadOnly,
            [ProbeWriteToolName] = VinxToolRisk.Write,
        };

    public static IReadOnlyCollection<string> AllowedToolNames { get; } = Allowlist.Keys.ToArray();

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
