using Microsoft.Extensions.AI;

namespace Vinx.Erp.AI;

/// <summary>
/// Contador da sonda inerte usado para provar que uma ferramenta protegida não
/// executa sem confirmação. Nenhum host registra essa sonda.
/// </summary>
internal sealed class HarnessProbeWriteCounter
{
    private int count;

    public int Count => Volatile.Read(ref count);

    internal string RecordWrite()
    {
        Interlocked.Increment(ref count);
        return "sonda de escrita registrada";
    }
}

/// <summary>Sondas sem dados do ERP usadas para verificar o harness real.</summary>
internal static class HarnessProbeTools
{
    internal static AIFunction CreateStatusTool() => ApplyPolicy(AIFunctionFactory.Create(
        GetHarnessProbeStatus,
        new AIFunctionFactoryOptions
        {
            Name = VinxToolPolicy.ProbeStatusToolName,
            Description = "Sonda somente leitura do harness VINX; retorna estado estático do servidor.",
        }));

    internal static AIFunction CreateWriteTool(HarnessProbeWriteCounter counter)
    {
        ArgumentNullException.ThrowIfNull(counter);
        return ApplyPolicy(AIFunctionFactory.Create(
            counter.RecordWrite,
            new AIFunctionFactoryOptions
            {
                Name = VinxToolPolicy.ProbeWriteToolName,
                Description = "Sonda de escrita do harness VINX; exige confirmação explícita do servidor.",
            }));
    }

    private static AIFunction ApplyPolicy(AIFunction function)
    {
        if (!VinxToolPolicy.TryClassify(function.Name, out var risk))
        {
            throw new InvalidOperationException($"Ferramenta '{function.Name}' não está na allowlist do VINX.");
        }

        return risk == VinxToolRisk.Write ? new ApprovalRequiredAIFunction(function) : function;
    }

    private static string GetHarnessProbeStatus() =>
        "vinx-harness-probe: módulo AI ativo, somente leitura, sem efeitos.";
}
