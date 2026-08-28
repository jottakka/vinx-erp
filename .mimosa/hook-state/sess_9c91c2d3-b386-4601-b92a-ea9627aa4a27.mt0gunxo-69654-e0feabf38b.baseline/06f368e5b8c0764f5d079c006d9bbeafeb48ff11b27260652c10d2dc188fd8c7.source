using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.AI;

/// <summary>
/// Camada fina sobre <see cref="AuthorizedCompanySetResolver"/>: obtém o
/// snapshot de autorização do ator e delega a decisão à política pura.
/// Snapshot indisponível é <see cref="AgentContextResolutionStatus.Unavailable"/>
/// fail-closed — nenhum contexto parcial é produzido. RLS e auditoria
/// pertencem à Foundation; o contexto mínimo nunca carrega prompt, JWT,
/// e-mail ou segredo.
/// </summary>
public sealed class AgentContextResolver(AuthorizedCompanySetResolver resolver)
    : IAgentContextResolver
{
    private const string ContextUnavailableReason = "context_unavailable";

    public async Task<AgentContextResolution> ResolveAsync(
        ActorContext actor,
        AgentTaskProfile profile,
        Guid? requestedCompanyId,
        CancellationToken cancellationToken)
    {
        var authorization = await resolver.ResolveSnapshotAsync(actor, cancellationToken);
        if (!authorization.IsSuccess)
        {
            return new AgentContextResolution(
                AgentContextResolutionStatus.Unavailable,
                ContextUnavailableReason);
        }

        return AgentPolicy.EvaluateContext(
            authorization.Value!,
            new AgentContextRequest(actor, profile, requestedCompanyId));
    }
}
