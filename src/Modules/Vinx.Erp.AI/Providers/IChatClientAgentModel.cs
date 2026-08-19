using Microsoft.Extensions.AI;

namespace Vinx.Erp.AI.Providers;

/// <summary>
/// Adapta um <see cref="IChatClient"/> aprovado à fronteira
/// <see cref="IAgentModel"/>: uma única chamada, single turn, sem tools e sem
/// loop. A selection apenas confirma o provider atribuído no servidor; o
/// modelo segue pinned no perfil do cliente aprovado — o request nunca escolhe
/// modelo na wire. Não autoriza, não redige e não persiste conversa.
/// </summary>
public sealed class IChatClientAgentModel(IChatClient chatClient) : IAgentModel
{
    public Task<ChatResponse> GetResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        AgentModelSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Options nulo preserva o pinning de modelo e limites do perfil aprovado.
        return chatClient.GetResponseAsync(messages, options: null, cancellationToken);
    }
}
