using System.Collections.Frozen;
using System.Collections.ObjectModel;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.AI.AgentHarness;

public enum AgentChannel
{
    Web = 1,
    WhatsApp = 2,
    Mcp = 3,
    Worker = 4
}

public enum AgentRunOutcomeKind
{
    Result = 1,
    Denied = 2,
    NeedsClarification = 3,
    RequiresConfirmation = 4,
    ValidationError = 5
}

public sealed record AgentContextItem
{
    public AgentContextItem(string kind, string content, double confidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidence));

        Kind = kind;
        Content = content;
        Confidence = confidence;
    }

    public string Kind { get; }
    public string Content { get; }
    public double Confidence { get; }
}

public sealed record AgentTaskProfile
{
    public AgentTaskProfile(
        string id,
        IEnumerable<string> allowedTools,
        int maxToolTurns,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(allowedTools);
        if (maxToolTurns <= 0) throw new ArgumentOutOfRangeException(nameof(maxToolTurns));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        Id = id;
        AllowedTools = FreezeNames(allowedTools, nameof(allowedTools));
        MaxToolTurns = maxToolTurns;
        Timeout = timeout;
    }

    public string Id { get; }
    public IReadOnlySet<string> AllowedTools { get; }
    public int MaxToolTurns { get; }
    public TimeSpan Timeout { get; }

    internal static IReadOnlySet<string> FreezeNames(IEnumerable<string> names, string parameterName)
    {
        var copy = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Names cannot be blank.", parameterName);

            copy.Add(name);
        }

        return copy.ToFrozenSet(StringComparer.Ordinal);
    }
}

public sealed record AgentExecutionContext
{
    public AgentExecutionContext(
        ActorContext actor,
        Guid? companyId,
        AgentChannel channel,
        IEnumerable<string> effectiveCapabilities,
        AgentTaskProfile profile)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(effectiveCapabilities);
        ArgumentNullException.ThrowIfNull(profile);
        if (actor.UserId == Guid.Empty || actor.CorrelationId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(actor));
        if (companyId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(companyId));
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));

        Actor = actor;
        CompanyId = companyId;
        Channel = channel;
        EffectiveCapabilities = AgentTaskProfile.FreezeNames(effectiveCapabilities, nameof(effectiveCapabilities));
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
    public AgentRunRequest(string userMessage, IEnumerable<AgentContextItem> externalContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(externalContext);

        var copy = externalContext.ToList();
        if (copy.Any(item => item is null)) throw new ArgumentException("External context cannot contain null items.", nameof(externalContext));

        UserMessage = userMessage;
        ExternalContext = new ReadOnlyCollection<AgentContextItem>(copy!);
    }

    public string UserMessage { get; }
    public IReadOnlyList<AgentContextItem> ExternalContext { get; }
}

public sealed record AgentRunOutcome
{
    public AgentRunOutcome(
        AgentRunOutcomeKind kind,
        string safeCode,
        string? assistantMessage,
        string? toolName)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));

        ArgumentException.ThrowIfNullOrWhiteSpace(safeCode);
        if (assistantMessage is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(assistantMessage);
        }

        if (toolName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
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
