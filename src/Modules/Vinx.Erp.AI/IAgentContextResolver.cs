using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.AI;

/// <summary>
/// Resolve o contexto confiável do agente: materializa o snapshot de
/// autorização do ator junto à Foundation e aplica a política fail-closed.
/// É o único passo antes de qualquer chamada de modelo.
/// </summary>
public interface IAgentContextResolver
{
    Task<AgentContextResolution> ResolveAsync(
        ActorContext actor,
        AgentTaskProfile profile,
        Guid? requestedCompanyId,
        CancellationToken cancellationToken);
}
