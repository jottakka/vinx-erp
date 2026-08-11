using System.Text.Json;
using Vinx.Erp.AI.AgentHarness;
using Vinx.Erp.AI.Tools;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class AgentRunPolicyTests
{
    [Theory]
    [InlineData(false, true, true, AgentToolRisk.ReadOnly, AgentRunOutcomeKind.Denied, "tool_not_allowed")]
    [InlineData(true, false, true, AgentToolRisk.ReadOnly, AgentRunOutcomeKind.NeedsClarification, "company_context_required")]
    [InlineData(true, true, false, AgentToolRisk.ReadOnly, AgentRunOutcomeKind.Denied, "capability_denied")]
    [InlineData(true, true, true, AgentToolRisk.InternalWrite, AgentRunOutcomeKind.RequiresConfirmation, "confirmation_required")]
    [InlineData(true, true, true, AgentToolRisk.ReadOnly, AgentRunOutcomeKind.Result, "tool_allowed")]
    public void Policy_uses_fixed_precedence(
        bool allowed,
        bool company,
        bool capability,
        AgentToolRisk risk,
        AgentRunOutcomeKind expectedKind,
        string expectedCode)
    {
        var decision = new AgentRunPolicy().Evaluate(
            CreateContext(allowed, company, capability),
            CreateDescriptor(risk));

        Assert.Equal(expectedKind, decision.OutcomeKind);
        Assert.Equal(expectedCode, decision.SafeCode);
    }

    private static AgentExecutionContext CreateContext(bool allowed, bool company, bool capability) => new(
        new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
        company ? Guid.NewGuid() : null,
        AgentChannel.Web,
        capability ? ["payments.read"] : [],
        new AgentTaskProfile("payments.summary", allowed ? ["payments.read"] : [], 2, TimeSpan.FromSeconds(30)));

    private static AgentToolDescriptor CreateDescriptor(AgentToolRisk risk)
    {
        using var inputSchema = JsonDocument.Parse("""{"type":"object"}""");
        using var outputSchema = JsonDocument.Parse("""{"type":"object"}""");

        return new AgentToolDescriptor(
            "payments.read",
            "1.0.0",
            inputSchema.RootElement,
            outputSchema.RootElement,
            "payments.read",
            risk,
            requiresExplicitCompany: true,
            requiresIdempotency: risk != AgentToolRisk.ReadOnly,
            new AgentToolMcpHints("Read payments", readOnlyHint: risk == AgentToolRisk.ReadOnly));
    }
}
