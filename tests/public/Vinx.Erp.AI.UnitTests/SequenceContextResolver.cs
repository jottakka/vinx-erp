using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.AI.UnitTests;

/// <summary>
/// Fake de <see cref="IAgentContextResolver"/>: devolve resoluções de uma
/// fila, uma por chamada, e conta as chamadas (lock: usado em testes
/// concorrentes). Helper <see cref="Resolved"/> constrói resolução de sucesso
/// com contexto mínimo da empresa informada.
/// </summary>
public sealed class SequenceContextResolver(params AgentContextResolution[] resolutions) : IAgentContextResolver
{
    private readonly Queue<AgentContextResolution> resolutions = new Queue<AgentContextResolution>(resolutions);
    private readonly Lock gate = new();

    public int CallCount { get; private set; }

    public List<Guid?> ReceivedCompanyIds { get; } = [];

    public Task<AgentContextResolution> ResolveAsync(
        ActorContext actor,
        AgentTaskProfile profile,
        Guid? requestedCompanyId,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            CallCount++;
            ReceivedCompanyIds.Add(requestedCompanyId);

            if (resolutions.Count == 0)
            {
                throw new InvalidOperationException("The fake resolver has no resolution left.");
            }

            return Task.FromResult(resolutions.Dequeue());
        }
    }

    public static AgentContextResolution Resolved(Guid companyId, string companyName = "Alfa") => new(
        AgentContextResolutionStatus.Resolved,
        "resolved",
        new AgentExecutionContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            companyId,
            companyName,
            Guid.NewGuid(),
            AgentTaskProfileCatalog.ReadOnlySummarizeProfileId,
            new HashSet<string> { AgentCapabilities.Read }));
}
