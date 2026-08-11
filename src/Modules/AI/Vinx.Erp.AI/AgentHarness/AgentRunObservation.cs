using Vinx.Erp.AI.Tools;

namespace Vinx.Erp.AI.AgentHarness;

public enum AgentRunPhase
{
    RunStarted = 1,
    ModelRequested = 2,
    ToolValidationStarted = 3,
    ToolInvocationStarted = 4,
    RunCompleted = 5
}

public sealed record AgentRunEvent
{
    public AgentRunEvent(
        Guid runId,
        Guid actorId,
        Guid? companyId,
        Guid correlationId,
        AgentChannel channel,
        string profileId,
        AgentRunPhase phase,
        string? toolName,
        AgentToolRisk? toolRisk,
        AgentRunOutcomeKind? outcomeKind)
    {
        if (runId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(runId));
        if (actorId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(actorId));
        if (companyId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(companyId));
        if (correlationId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(correlationId));
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        if (toolName is not null) ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (toolRisk is not null && !Enum.IsDefined(toolRisk.Value)) throw new ArgumentOutOfRangeException(nameof(toolRisk));

        if (phase == AgentRunPhase.RunCompleted && outcomeKind is null)
        {
            throw new ArgumentException("Completed events require an outcome kind.", nameof(outcomeKind));
        }

        if (phase != AgentRunPhase.RunCompleted && outcomeKind is not null)
        {
            throw new ArgumentException("Only completed events may include an outcome kind.", nameof(outcomeKind));
        }

        if (outcomeKind is not null && !Enum.IsDefined(outcomeKind.Value)) throw new ArgumentOutOfRangeException(nameof(outcomeKind));

        RunId = runId;
        ActorId = actorId;
        CompanyId = companyId;
        CorrelationId = correlationId;
        Channel = channel;
        ProfileId = profileId;
        Phase = phase;
        ToolName = toolName;
        ToolRisk = toolRisk;
        OutcomeKind = outcomeKind;
    }

    public Guid RunId { get; }
    public Guid ActorId { get; }
    public Guid? CompanyId { get; }
    public Guid CorrelationId { get; }
    public AgentChannel Channel { get; }
    public string ProfileId { get; }
    public AgentRunPhase Phase { get; }
    public string? ToolName { get; }
    public AgentToolRisk? ToolRisk { get; }
    public AgentRunOutcomeKind? OutcomeKind { get; }
}

public interface IAgentRunObserver
{
    void Observe(AgentRunEvent runEvent);
}
