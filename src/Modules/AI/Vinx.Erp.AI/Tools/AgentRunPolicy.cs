using Vinx.Erp.AI.AgentHarness;

namespace Vinx.Erp.AI.Tools;

public sealed record AgentPolicyDecision
{
    public AgentPolicyDecision(AgentRunOutcomeKind outcomeKind, string safeCode)
    {
        if (!Enum.IsDefined(outcomeKind)) throw new ArgumentOutOfRangeException(nameof(outcomeKind));
        ArgumentException.ThrowIfNullOrWhiteSpace(safeCode);

        OutcomeKind = outcomeKind;
        SafeCode = safeCode;
    }

    public AgentRunOutcomeKind OutcomeKind { get; }
    public string SafeCode { get; }
}

public sealed class AgentRunPolicy
{
    public AgentPolicyDecision Evaluate(AgentExecutionContext context, AgentToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!context.Profile.AllowedTools.Contains(descriptor.Name))
        {
            return new AgentPolicyDecision(AgentRunOutcomeKind.Denied, "tool_not_allowed");
        }

        if (descriptor.RequiresExplicitCompany && context.CompanyId is null)
        {
            return new AgentPolicyDecision(AgentRunOutcomeKind.NeedsClarification, "company_context_required");
        }

        if (!context.EffectiveCapabilities.Contains(descriptor.RequiredCapability))
        {
            return new AgentPolicyDecision(AgentRunOutcomeKind.Denied, "capability_denied");
        }

        if (descriptor.Risk != AgentToolRisk.ReadOnly)
        {
            return new AgentPolicyDecision(AgentRunOutcomeKind.RequiresConfirmation, "confirmation_required");
        }

        return new AgentPolicyDecision(AgentRunOutcomeKind.Result, "tool_allowed");
    }
}
