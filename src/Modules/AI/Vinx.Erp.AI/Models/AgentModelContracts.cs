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

        if (allowedTools.Any(tool => tool is null))
        {
            throw new ArgumentException("Allowed tool descriptors cannot be null.", nameof(allowedTools));
        }

        if (priorToolResults.Any(result => result is null))
        {
            throw new ArgumentException("Prior tool results cannot be null.", nameof(priorToolResults));
        }

        Request = request;
        AllowedTools = Array.AsReadOnly(allowedTools.ToArray());
        PriorToolResults = Array.AsReadOnly(priorToolResults.ToArray());
    }

    public AgentRunRequest Request { get; }
    public IReadOnlyList<AgentToolDescriptor> AllowedTools { get; }
    public IReadOnlyList<AgentToolResult> PriorToolResults { get; }
}

public sealed record AgentModelTurn
{
    public AgentModelTurn(string? assistantMessage, AgentToolCall? toolCall)
    {
        if ((assistantMessage is null) == (toolCall is null))
        {
            throw new ArgumentException("A model turn must contain either an assistant message or a tool call.");
        }

        if (assistantMessage is not null && string.IsNullOrWhiteSpace(assistantMessage))
        {
            throw new ArgumentException("Assistant message cannot be blank.", nameof(assistantMessage));
        }

        AssistantMessage = assistantMessage;
        ToolCall = toolCall;
    }

    public string? AssistantMessage { get; }
    public AgentToolCall? ToolCall { get; }
}
