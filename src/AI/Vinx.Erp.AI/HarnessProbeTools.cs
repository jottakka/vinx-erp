using Microsoft.Extensions.AI;

namespace Vinx.Erp.AI;

/// <summary>
/// Contador de efeito colateral da sonda de escrita. Serve apenas para provar
/// em testes que a ferramenta de escrita só executa após confirmação do
/// servidor; não acessa nenhum dado do ERP. A escrita é idempotente por
/// <c>RequestId</c> de execução: um replay com o mesmo <c>RequestId</c> devolve
/// o resultado original sem repetir a mutação.
/// </summary>
public sealed class HarnessProbeWriteCounter
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, string> resultsByRequestId = new();
    private int count;

    public int Count => Volatile.Read(ref count);

    public string RecordWrite(Guid requestId)
    {
        lock (gate)
        {
            if (resultsByRequestId.TryGetValue(requestId, out var existing))
            {
                return existing;
            }

            count++;
            var result = $"sonda de escrita registrada para {requestId}";
            resultsByRequestId[requestId] = result;
            return result;
        }
    }
}

/// <summary>
/// Duas ferramentas de sonda inertes (sem dados do ERP, sem efeitos reais e
/// não registradas em nenhum host): uma leitura executada diretamente pelo
/// framework e uma escrita que só executa com confirmação do servidor. A
/// ferramenta de escrita recebe o <c>RequestId</c> de execução do coordenador,
/// nunca do modelo, e é idempotente por esse identificador.
/// </summary>
public static class HarnessProbeTools
{
    public static AIFunction CreateStatusTool() => ApplyPolicy(AIFunctionFactory.Create(
        GetHarnessProbeStatus,
        new AIFunctionFactoryOptions
        {
            Name = VinxToolPolicy.ProbeStatusToolName,
            Description = "Sonda somente leitura do harness VINX; retorna estado estático do servidor.",
        }));

    public static AIFunction CreateWriteTool(HarnessProbeWriteCounter counter, Guid executionRequestId) =>
        ApplyPolicy(AIFunctionFactory.Create(
            () => counter.RecordWrite(executionRequestId),
            new AIFunctionFactoryOptions
            {
                Name = VinxToolPolicy.ProbeWriteToolName,
                Description = "Sonda de escrita do harness VINX; exige confirmação explícita do servidor.",
            }));

    /// <summary>
    /// O registro dirige o envolvimento pela classificação da allowlist:
    /// ferramentas de escrita são envolvidas em <see cref="ApprovalRequiredAIFunction"/>,
    /// leituras executam diretamente e nomes fora da allowlist lançam erro.
    /// Assim classificação e envolvimento não divergem.
    /// </summary>
    private static AIFunction ApplyPolicy(AIFunction function)
    {
        if (!VinxToolPolicy.TryClassify(function.Name, out var risk))
        {
            throw new InvalidOperationException(
                $"Ferramenta '{function.Name}' não está na allowlist do VINX.");
        }

        return risk == VinxToolRisk.Write ? new ApprovalRequiredAIFunction(function) : function;
    }

    private static string GetHarnessProbeStatus() =>
        "vinx-harness-probe: módulo AI ativo, somente leitura, sem efeitos.";
}
