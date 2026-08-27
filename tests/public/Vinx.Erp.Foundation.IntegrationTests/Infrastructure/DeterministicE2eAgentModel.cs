using Microsoft.Extensions.AI;
using Vinx.Erp.AI;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

/// <summary>
/// Modelo fixo exclusivo da demonstração E2E compartilhada. Guarda somente
/// metadados seguros necessários para provar a ordem autorização-modelo.
/// </summary>
public sealed class DeterministicE2eAgentModel : IAgentModel
{
    public const string ResponseText = "[Demonstração E2E] Resposta determinística do modelo de teste.";

    private int _callCount;
    private int _lastMessageCount;
    private AgentModelSelection? _lastSelection;

    public int CallCount => Volatile.Read(ref _callCount);

    public int LastMessageCount => Volatile.Read(ref _lastMessageCount);

    public AgentModelSelection? LastSelection => Volatile.Read(ref _lastSelection);

    public Task<ChatResponse> GetResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        AgentModelSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();

        Interlocked.Increment(ref _callCount);
        Interlocked.Exchange(ref _lastMessageCount, messages.Count);
        Volatile.Write(ref _lastSelection, selection);

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseText)));
    }
}
