using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Vinx.Erp.AI;

/// <summary>
/// Superfície opaca do harness. Expõe somente criação de sessão e turnos
/// textuais; o agente bruto e respostas de aprovação permanecem inacessíveis.
/// </summary>
public sealed class HarnessAgentBundle
{
    private readonly AIAgent agent;

    internal HarnessAgentBundle(AIAgent agent) =>
        this.agent = agent ?? throw new ArgumentNullException(nameof(agent));

    public async Task<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default) =>
        await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

    public Task<AgentResponse> RunUserTurnAsync(
        AgentSession session,
        string userText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);

        return agent.RunAsync(
            new ChatMessage(ChatRole.User, userText),
            session,
            cancellationToken: cancellationToken);
    }
}
