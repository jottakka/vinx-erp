using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Vinx.Erp.AI;

/// <summary>
/// Fonte do <c>RequestId</c> de execução da escrita do harness. O coordenador
/// (código do servidor) é o único dono do vínculo: <see cref="Bind"/> acontece
/// uma única vez no registro da proposta e <see cref="Resolve"/> falha fechado
/// enquanto não houver vínculo. Nenhuma API pública constrói, vincula ou
/// reutiliza a fonte.
/// </summary>
internal sealed class HarnessExecutionRequestIdSource
{
    private Guid requestId;
    // 0 = unbound, 1 = binding, 2 = bound. Publishing the terminal state only
    // after the value prevents Resolve from seeing Guid.Empty as a bound id.
    private int bindingState;

    internal HarnessExecutionRequestIdSource()
    {
    }

    internal bool IsBound => Volatile.Read(ref bindingState) == 2;

    internal void Bind(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("O RequestId de execução não pode ser vazio.", nameof(value));
        }

        if (Interlocked.CompareExchange(ref bindingState, 1, 0) != 0)
        {
            throw new InvalidOperationException("O RequestId de execução já está vinculado.");
        }

        requestId = value;
        Volatile.Write(ref bindingState, 2);
    }

    internal Guid Resolve()
    {
        if (Volatile.Read(ref bindingState) != 2)
        {
            throw new InvalidOperationException(
                "O RequestId de execução não está vinculado (fail-closed).");
        }

        return requestId;
    }
}

/// <summary>
/// Feixe opaco do harness: agente + fonte de <c>RequestId</c> de execução
/// emparelhados pela fábrica. A superfície pública expõe apenas criação de
/// sessão e turno de usuário textual; nenhuma API pública expõe o agente cru,
/// <c>RunAsync</c>, entrada de resposta de aprovação ou a fonte de
/// <c>RequestId</c>.
/// </summary>
public sealed class HarnessAgentBundle
{
    internal HarnessAgentBundle(AIAgent agent, HarnessExecutionRequestIdSource requestIdSource)
    {
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        RequestIdSource = requestIdSource ?? throw new ArgumentNullException(nameof(requestIdSource));
    }

    internal AIAgent Agent { get; }

    internal HarnessExecutionRequestIdSource RequestIdSource { get; }

    public async Task<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default) =>
        await Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Executa um turno do usuário com entrada exclusivamente textual. Mensagens
    /// com resposta de aprovação são rejeitadas (fail-closed): a única rota de
    /// entrega de aprovação é o coordenador, após claim durável.
    /// </summary>
    public async Task<AgentResponse> RunUserTurnAsync(
        AgentSession session,
        string userText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);

        var message = new ChatMessage(ChatRole.User, userText);
        foreach (var content in message.Contents)
        {
            if (content is ToolApprovalResponseContent)
            {
                throw new InvalidOperationException(
                    "Resposta de aprovação não pode ser entregue como turno de usuário (fail-closed).");
            }
        }

        return await Agent.RunAsync(message, session, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
