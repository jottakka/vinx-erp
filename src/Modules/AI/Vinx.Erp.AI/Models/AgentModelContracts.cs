using System.Collections.ObjectModel;
using Vinx.Erp.AI.AgentHarness;
using Vinx.Erp.AI.Tools;

namespace Vinx.Erp.AI.Models;

public interface IAgentModel
{
    Task<AgentModelTurn> CompleteAsync(
        AgentModelRequest request,
        CancellationToken cancellationToken);
}

public sealed record AgentModelRequest
{
    public AgentModelRequest(
        AgentRunRequest request,
        IReadOnlyList<AgentToolDescriptor> allowedTools,
        IReadOnlyList<AgentToolResult> priorToolResults)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowedTools);
        ArgumentNullException.ThrowIfNull(priorToolResults);

        Request = request;
        AllowedTools = Copy(allowedTools, nameof(allowedTools));
        PriorToolResults = Copy(priorToolResults, nameof(priorToolResults));
    }

    public AgentRunRequest Request { get; }
    public IReadOnlyList<AgentToolDescriptor> AllowedTools { get; }
    public IReadOnlyList<AgentToolResult> PriorToolResults { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> items, string parameterName)
    {
        var copy = items.ToList();
        if (copy.Any(item => item is null)) throw new ArgumentException("Items cannot contain null values.", parameterName);

        return new ReadOnlyCollection<T>(copy!);
    }
}

public sealed record AgentModelTurn
{
    public AgentModelTurn(string? assistantMessage, AgentToolCall? toolCall)
    {
        if ((assistantMessage is null) == (toolCall is null))
        {
            throw new ArgumentException("A model turn must have exactly one final message or tool call.");
        }

        if (assistantMessage is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(assistantMessage);
        }

        AssistantMessage = assistantMessage;
        ToolCall = toolCall;
    }

    public string? AssistantMessage { get; }
    public AgentToolCall? ToolCall { get; }

    public static AgentModelTurn Final(string assistantMessage) => new(assistantMessage, null);

    public static AgentModelTurn Tool(AgentToolCall toolCall) => new(null, toolCall);
}
