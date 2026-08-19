using Microsoft.Extensions.AI;
using Vinx.Erp.AI;

namespace Vinx.Erp.AI.UnitTests;

/// <summary>
/// Fake de <see cref="IAgentModel"/>: devolve respostas em sequência e
/// registra as mensagens e a selection recebidas. Fábricas cobrem os modos
/// texto, tool call, exceção e atraso sensível ao token (timeout/cancelamento).
/// </summary>
public sealed class TestAgentModel : IAgentModel
{
    private readonly Queue<ChatResponse> responses;
    private readonly Exception? exception;
    private readonly TimeSpan delay;

    public TestAgentModel(params ChatResponse[] responses)
    {
        this.responses = new Queue<ChatResponse>(responses);
    }

    private TestAgentModel(Exception exception)
        : this()
    {
        this.exception = exception;
    }

    private TestAgentModel(TimeSpan delay, string text)
        : this(TextResponse(text))
    {
        this.delay = delay;
    }

    public int CallCount { get; private set; }

    public List<IReadOnlyList<ChatMessage>> ReceivedMessages { get; } = [];

    public List<AgentModelSelection> ReceivedSelections { get; } = [];

    public static TestAgentModel Text(string text) => new(TextResponse(text));

    public static TestAgentModel ToolCall(string toolName) => new(new ChatResponse(new ChatMessage(
        ChatRole.Assistant,
        new AIContent[] { new FunctionCallContent("call_1", toolName, new Dictionary<string, object?>()) })));

    public static TestAgentModel Throwing(Exception exception) => new(exception);

    public static TestAgentModel Delayed(TimeSpan delay, string text = "") => new(delay, text);

    public async Task<ChatResponse> GetResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        AgentModelSelection selection,
        CancellationToken cancellationToken)
    {
        CallCount++;
        ReceivedMessages.Add(messages);
        ReceivedSelections.Add(selection);

        if (exception is not null)
        {
            throw exception;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        if (responses.Count == 0)
        {
            throw new InvalidOperationException("The fake model has no response left.");
        }

        return responses.Dequeue();
    }

    private static ChatResponse TextResponse(string text) => new(new ChatMessage(ChatRole.Assistant, text));
}
