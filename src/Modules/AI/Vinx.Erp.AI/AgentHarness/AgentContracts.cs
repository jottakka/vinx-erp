using System.Collections.Frozen;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.AI.AgentHarness;

public sealed record AgentExecutionContext
{
    public AgentExecutionContext(
        ActorContext actor,
        Guid? companyId,
        AgentChannel channel,
        IReadOnlySet<string> effectiveCapabilities,
        AgentTaskProfile profile)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(effectiveCapabilities);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfEqual(actor.UserId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(actor.CorrelationId, Guid.Empty);

        if (companyId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(companyId), companyId, "Company ID cannot be empty.");
        }

        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unsupported agent channel.");
        }

        if (effectiveCapabilities.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Capability names cannot be blank.", nameof(effectiveCapabilities));
        }

        Actor = actor;
        CompanyId = companyId;
        Channel = channel;
        EffectiveCapabilities = effectiveCapabilities.ToFrozenSet(StringComparer.Ordinal);
        Profile = profile;
    }

    public ActorContext Actor { get; }
    public Guid? CompanyId { get; }
    public AgentChannel Channel { get; }
    public IReadOnlySet<string> EffectiveCapabilities { get; }
    public AgentTaskProfile Profile { get; }
}

public sealed record AgentRunRequest
{
    public AgentRunRequest(
        string userMessage,
        IReadOnlyList<AgentContextItem> externalContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(externalContext);

        if (externalContext.Any(item => item is null))
        {
            throw new ArgumentException("External context items cannot be null.", nameof(externalContext));
        }

        UserMessage = userMessage;
        ExternalContext = Array.AsReadOnly(externalContext.ToArray());
    }

    public string UserMessage { get; }
    public IReadOnlyList<AgentContextItem> ExternalContext { get; }
}

public sealed record AgentContextItem
{
    public AgentContextItem(string kind, string content, double? confidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        if (confidence is { } value && (double.IsNaN(value) || value < 0 || value > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Confidence must be between zero and one.");
        }

        Kind = kind;
        Content = content;
        Confidence = confidence;
    }

    public string Kind { get; }
    public string Content { get; }
    public double? Confidence { get; }
}

public sealed record AgentTaskProfile
{
    public AgentTaskProfile(
        string id,
        int maxToolTurns,
        TimeSpan timeout,
        IReadOnlySet<string> allowedTools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(allowedTools);

        if (maxToolTurns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxToolTurns), maxToolTurns, "Maximum tool turns must be positive.");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        if (allowedTools.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Allowed tool names cannot be blank.", nameof(allowedTools));
        }

        Id = id;
        MaxToolTurns = maxToolTurns;
        Timeout = timeout;
        AllowedTools = allowedTools.ToFrozenSet(StringComparer.Ordinal);
    }

    public string Id { get; }
    public int MaxToolTurns { get; }
    public TimeSpan Timeout { get; }
    public IReadOnlySet<string> AllowedTools { get; }
}

public sealed record AgentRunOutcome
{
    public AgentRunOutcome(
        AgentRunOutcomeKind kind,
        string safeCode,
        string? assistantMessage,
        string? toolName)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported agent run outcome.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(safeCode);

        if (toolName is not null && string.IsNullOrWhiteSpace(toolName))
        {
            throw new ArgumentException("Tool name cannot be blank.", nameof(toolName));
        }

        Kind = kind;
        SafeCode = safeCode;
        AssistantMessage = assistantMessage;
        ToolName = toolName;
    }

    public AgentRunOutcomeKind Kind { get; }
    public string SafeCode { get; }
    public string? AssistantMessage { get; }
    public string? ToolName { get; }
}

public enum AgentRunOutcomeKind
{
    Result,
    RequiresConfirmation,
    Denied,
    Conflict,
    ValidationError,
    NeedsClarification
}

public enum AgentChannel
{
    Web,
    WhatsApp,
    Mcp,
    Worker
}
