using Microsoft.Extensions.AI;

namespace Vinx.Erp.AI;

/// <summary>
/// Fronteira Microsoft.Extensions.AI do provedor de modelo: o runner normaliza
/// turns e outcomes, a implementação traduz para o SDK do provedor (#135).
/// Não recebe ferramentas — tool call volta como proposta que exige
/// confirmação e nunca é executada aqui.
/// </summary>
public interface IAgentModel
{
    Task<ChatResponse> GetResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        AgentModelSelection selection,
        CancellationToken cancellationToken);
}
