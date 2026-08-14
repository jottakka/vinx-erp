using Microsoft.Extensions.AI;

namespace Vinx.Erp.AI.UnitTests;

/// <summary>Uma chamada registrada do agente ao modelo falso.</summary>
internal sealed record RecordedRequest(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<string> ToolNames);

/// <summary>
/// Cliente de chat determinístico derivado de <see cref="DelegatingChatClient"/>
/// (somente para testes). Devolve respostas roteirizadas contendo
/// <see cref="FunctionCallContent"/> e registra cada requisição enviada.
/// </summary>
internal sealed class ScriptedChatClient : DelegatingChatClient
{
    private readonly Queue<ChatMessage> scripted;

    public ScriptedChatClient(params ChatMessage[] scriptedResponses)
        : base(new UnreachableChatClient())
    {
        scripted = new Queue<ChatMessage>(scriptedResponses);
    }

    public List<RecordedRequest> Requests { get; } = [];

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(new RecordedRequest(
            chatMessages.ToList(),
            options?.Tools?.Select(tool => tool.Name).ToList() ?? []));

        var message = scripted.Count > 0
            ? scripted.Dequeue()
            : new ChatMessage(ChatRole.Assistant, "fim do roteiro");

        var response = new ChatResponse(message)
        {
            FinishReason = message.Contents.Any(content => content is FunctionCallContent)
                ? ChatFinishReason.ToolCalls
                : ChatFinishReason.Stop,
        };

        return Task.FromResult(response);
    }
}

/// <summary>Cliente interno que nunca deve ser chamado pelo teste.</summary>
internal sealed class UnreachableChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("O cliente interno não deve ser chamado.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("O cliente interno não deve ser chamado.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
