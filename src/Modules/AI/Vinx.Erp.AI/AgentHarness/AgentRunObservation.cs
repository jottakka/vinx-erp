using Vinx.Erp.AI.Tools;

namespace Vinx.Erp.AI.AgentHarness;

public interface IAgentRunObserver
{
    ValueTask ObserveAsync(AgentRunEvent runEvent, CancellationToken cancellationToken);
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
        string? toolName,
        AgentToolRisk? toolRisk,
        AgentRunPhase phase,
        AgentRunOutcomeKind? outcome)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(runId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(actorId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(correlationId, Guid.Empty);

        if (companyId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(companyId), companyId, "Company ID cannot be empty.");
        }

        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unsupported agent channel.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        if (toolName is not null && string.IsNullOrWhiteSpace(toolName))
        {
            throw new ArgumentException("Tool name cannot be blank.", nameof(toolName));
        }

        if (toolRisk is not null && !Enum.IsDefined(toolRisk.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(toolRisk), toolRisk, "Unsupported tool risk.");
        }

        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unsupported agent run phase.");
        }

        if (outcome is not null && !Enum.IsDefined(outcome.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported agent run outcome.");
        }

        RunId = runId;
        ActorId = actorId;
        CompanyId = companyId;
        CorrelationId = correlationId;
        Channel = channel;
        ProfileId = profileId;
        ToolName = toolName;
        ToolRisk = toolRisk;
        Phase = phase;
        Outcome = outcome;
    }

    public Guid RunId { get; }
    public Guid ActorId { get; }
    public Guid? CompanyId { get; }
    public Guid CorrelationId { get; }
    public AgentChannel Channel { get; }
    public string ProfileId { get; }
    public string? ToolName { get; }
    public AgentToolRisk? ToolRisk { get; }
    public AgentRunPhase Phase { get; }
    public AgentRunOutcomeKind? Outcome { get; }
}

public enum AgentRunPhase
{
    Received,
    ModelRequested,
    ToolProposed,
    ToolValidated,
    ToolInvoked,
    Completed
}
