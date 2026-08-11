using Vinx.Erp.AI.AgentHarness;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class AgentContractsTests
{
    [Fact]
    public void Valid_construction_preserves_public_contract_values()
    {
        var actor = new ActorContext(
            Guid.Parse("4ab9cb1e-c4c2-4d63-84f6-a6ce4e7d3d69"),
            Guid.Parse("5d19ea98-79a3-4db8-b265-034ab1d04e7d"));
        var companyId = Guid.Parse("f91d8d79-1073-405e-93a4-41eb31b12a93");
        var profile = new AgentTaskProfile("paguai.summary", ["paguai.read"], 4, TimeSpan.FromSeconds(45));
        var context = new AgentExecutionContext(
            actor, companyId, AgentChannel.WhatsApp, ["paguai.read", "PAGUAI.READ"], profile);
        var item = new AgentContextItem("document", "Invoice needs review", 0.75);
        var request = new AgentRunRequest("Summarize invoice", [item]);
        var outcome = new AgentRunOutcome(
            AgentRunOutcomeKind.RequiresConfirmation,
            "confirmation_required",
            "Confirmation is required.",
            "paguai.submit");

        Assert.Equal(actor, context.Actor);
        Assert.Equal(companyId, context.CompanyId);
        Assert.Equal(AgentChannel.WhatsApp, context.Channel);
        Assert.Equal(2, context.EffectiveCapabilities.Count);
        Assert.Contains("paguai.read", context.EffectiveCapabilities);
        Assert.Contains("PAGUAI.READ", context.EffectiveCapabilities);
        Assert.Equal(profile, context.Profile);
        Assert.Equal("paguai.summary", profile.Id);
        Assert.Contains("paguai.read", profile.AllowedTools);
        Assert.Equal(4, profile.MaxToolTurns);
        Assert.Equal(TimeSpan.FromSeconds(45), profile.Timeout);
        Assert.Equal("document", item.Kind);
        Assert.Equal("Invoice needs review", item.Content);
        Assert.Equal(0.75, item.Confidence);
        Assert.Equal("Summarize invoice", request.UserMessage);
        Assert.Equal(item, Assert.Single(request.ExternalContext));
        Assert.Equal(AgentRunOutcomeKind.RequiresConfirmation, outcome.Kind);
        Assert.Equal("confirmation_required", outcome.SafeCode);
        Assert.Equal("Confirmation is required.", outcome.AssistantMessage);
        Assert.Equal("paguai.submit", outcome.ToolName);
    }

    [Fact]
    public void Execution_context_copies_capabilities_and_rejects_empty_actor()
    {
        var capabilities = new HashSet<string>(["paguai.read"], StringComparer.Ordinal);
        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());

        var context = new AgentExecutionContext(
            actor, Guid.NewGuid(), AgentChannel.Web, capabilities, CreateProfile());

        capabilities.Clear();

        Assert.Contains("paguai.read", context.EffectiveCapabilities);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentExecutionContext(
            new ActorContext(Guid.Empty, Guid.NewGuid()),
            Guid.NewGuid(), AgentChannel.Web, new HashSet<string>(), CreateProfile()));
    }

    [Fact]
    public void Execution_context_rejects_untrusted_identifiers_and_unknown_channel()
    {
        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());

        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentExecutionContext(
            new ActorContext(Guid.NewGuid(), Guid.Empty), null, AgentChannel.Web, new HashSet<string>(), CreateProfile()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentExecutionContext(
            actor, Guid.Empty, AgentChannel.Web, new HashSet<string>(), CreateProfile()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentExecutionContext(
            actor, null, (AgentChannel)99, new HashSet<string>(), CreateProfile()));
        Assert.Throws<ArgumentException>(() => new AgentExecutionContext(
            actor, null, AgentChannel.Web, new HashSet<string>([" "], StringComparer.Ordinal), CreateProfile()));
    }

    [Fact]
    public void Run_request_copies_external_context_and_rejects_invalid_input()
    {
        var externalContext = new List<AgentContextItem>
        {
            new("document", "Invoice needs review", 0.8)
        };

        var request = new AgentRunRequest("Summarize status", externalContext);
        externalContext.Clear();

        Assert.Single(request.ExternalContext);
        Assert.Throws<ArgumentException>(() => new AgentRunRequest(" ", []));
        Assert.Throws<ArgumentException>(() => new AgentRunRequest("Summarize status", [null!]));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Context_item_rejects_invalid_confidence(double confidence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AgentContextItem("document", "Invoice needs review", confidence));
    }

    [Fact]
    public void Context_item_rejects_blank_kind_or_content()
    {
        Assert.Throws<ArgumentException>(() => new AgentContextItem(" ", "Invoice needs review", 0.8));
        Assert.Throws<ArgumentException>(() => new AgentContextItem("document", " ", 0.8));
    }

    [Fact]
    public void Task_profile_copies_allowed_tools_and_rejects_invalid_values()
    {
        var allowedTools = new HashSet<string>(["paguai.read"], StringComparer.Ordinal);
        var profile = new AgentTaskProfile("paguai.summary", allowedTools, 3, TimeSpan.FromSeconds(30));
        allowedTools.Clear();

        Assert.Contains("paguai.read", profile.AllowedTools);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTaskProfile(
            "paguai.summary", new HashSet<string>(), 0, TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTaskProfile(
            "paguai.summary", new HashSet<string>(), 3, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new AgentTaskProfile(
            "paguai.summary", new HashSet<string>([" "], StringComparer.Ordinal), 3, TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentException>(() => new AgentTaskProfile(
            " ", new HashSet<string>(), 3, TimeSpan.FromSeconds(30)));

        var ordinalProfile = new AgentTaskProfile("paguai.summary", ["tool", "TOOL"], 3, TimeSpan.FromSeconds(30));
        Assert.Equal(2, ordinalProfile.AllowedTools.Count);
        Assert.Contains("tool", ordinalProfile.AllowedTools);
        Assert.Contains("TOOL", ordinalProfile.AllowedTools);
    }

    [Fact]
    public void Run_outcome_rejects_unknown_kind_and_blank_safe_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRunOutcome(
            (AgentRunOutcomeKind)99, "completed", null, null));
        Assert.Throws<ArgumentException>(() => new AgentRunOutcome(
            AgentRunOutcomeKind.Result, " ", null, null));
        Assert.Throws<ArgumentException>(() => new AgentRunOutcome(
            AgentRunOutcomeKind.Result, "completed", " ", null));
        Assert.Throws<ArgumentException>(() => new AgentRunOutcome(
            AgentRunOutcomeKind.Result, "completed", null, " "));
    }

    private static AgentTaskProfile CreateProfile() => new(
        "paguai.summary",
        new HashSet<string>(["paguai.read"], StringComparer.Ordinal),
        3,
        TimeSpan.FromSeconds(30));
}
