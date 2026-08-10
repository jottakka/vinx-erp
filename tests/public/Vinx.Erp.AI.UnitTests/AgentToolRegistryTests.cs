using System.Reflection;
using System.Text.Json;
using Vinx.Erp.AI.AgentHarness;
using Vinx.Erp.AI.Tools;
using Vinx.Erp.AI.UnitTests.Fixtures;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class AgentToolRegistryTests
{
    [Fact]
    public void Registry_rejects_duplicate_tool_names()
    {
        var duplicate = new RecordingAgentTool("paguai.read");

        Assert.Throws<ArgumentException>(() => new AgentToolRegistry([duplicate, duplicate]));
    }

    [Fact]
    public void Registry_lookup_is_case_sensitive()
    {
        var tool = new RecordingAgentTool("paguai.read");
        var registry = new AgentToolRegistry([tool]);

        Assert.True(registry.TryGet("paguai.read", out var resolved));
        Assert.Same(tool, resolved.Tool);
        Assert.False(registry.TryGet("PAGUAI.READ", out _));
    }

    [Fact]
    public void Registry_is_unchanged_when_source_collection_changes()
    {
        var tool = new RecordingAgentTool("paguai.read");
        var source = new List<IAgentTool> { tool };
        var registry = new AgentToolRegistry(source);

        source.Clear();
        source.Add(new RecordingAgentTool("paguai.other"));

        Assert.True(registry.TryGet("paguai.read", out var resolved));
        Assert.Same(tool, resolved.Tool);
        Assert.False(registry.TryGet("paguai.other", out _));
    }

    [Fact]
    public void Registry_lookup_preserves_descriptor_captured_at_construction()
    {
        var tool = new RecordingAgentTool("paguai.read");
        var registry = new AgentToolRegistry([tool]);

        tool.ReplaceDescriptor(CreateDescriptor(
            AgentToolRisk.Irreversible,
            requiresExplicitCompany: true));

        Assert.True(registry.TryGet("paguai.read", out var resolved));
        Assert.Equal("paguai.read", resolved.Descriptor.Name);
        Assert.Equal(AgentToolRisk.ReadOnly, resolved.Descriptor.Risk);
    }

    [Fact]
    public void Policy_denies_tool_outside_profile_before_other_checks()
    {
        var context = CreateContext(companyId: null, capabilities: [], allowedTools: []);

        var decision = new AgentRunPolicy().Evaluate(
            context,
            CreateDescriptor(AgentToolRisk.Financial, requiresExplicitCompany: true));

        Assert.Equal(AgentRunOutcomeKind.Denied, decision.OutcomeKind);
        Assert.Equal("tool_not_allowed", decision.SafeCode);
    }

    [Fact]
    public void Policy_requires_company_before_capability_or_confirmation()
    {
        var context = CreateContext(companyId: null, capabilities: [], allowedTools: ["paguai.read"]);

        var decision = new AgentRunPolicy().Evaluate(
            context,
            CreateDescriptor(AgentToolRisk.Financial, requiresExplicitCompany: true));

        Assert.Equal(AgentRunOutcomeKind.NeedsClarification, decision.OutcomeKind);
        Assert.Equal("company_context_required", decision.SafeCode);
    }

    [Fact]
    public void Policy_denies_missing_capability_before_confirmation()
    {
        var context = CreateContext(Guid.NewGuid(), capabilities: [], allowedTools: ["paguai.read"]);

        var decision = new AgentRunPolicy().Evaluate(
            context,
            CreateDescriptor(AgentToolRisk.Financial, requiresExplicitCompany: true));

        Assert.Equal(AgentRunOutcomeKind.Denied, decision.OutcomeKind);
        Assert.Equal("capability_denied", decision.SafeCode);
    }

    [Theory]
    [InlineData(AgentToolRisk.InternalWrite)]
    [InlineData(AgentToolRisk.ExternalWrite)]
    [InlineData(AgentToolRisk.Financial)]
    [InlineData(AgentToolRisk.Irreversible)]
    public void Policy_requires_confirmation_before_any_write(AgentToolRisk risk)
    {
        var context = CreateContext(
            Guid.NewGuid(),
            capabilities: ["paguai.read"],
            allowedTools: ["paguai.read"]);

        var decision = new AgentRunPolicy().Evaluate(
            context,
            CreateDescriptor(risk, requiresExplicitCompany: true));

        Assert.Equal(AgentRunOutcomeKind.RequiresConfirmation, decision.OutcomeKind);
        Assert.Equal("confirmation_required", decision.SafeCode);
    }

    [Fact]
    public void Policy_allows_read_after_all_trusted_checks_pass()
    {
        var context = CreateContext(
            Guid.NewGuid(),
            capabilities: ["paguai.read"],
            allowedTools: ["paguai.read"]);

        var decision = new AgentRunPolicy().Evaluate(
            context,
            CreateDescriptor(AgentToolRisk.ReadOnly, requiresExplicitCompany: true));

        Assert.Equal(AgentRunOutcomeKind.Result, decision.OutcomeKind);
        Assert.Equal("tool_allowed", decision.SafeCode);
    }

    [Fact]
    public void Policy_evaluate_accepts_only_trusted_context_and_descriptor()
    {
        var evaluate = Assert.Single(typeof(AgentRunPolicy).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));

        Assert.Equal(nameof(AgentRunPolicy.Evaluate), evaluate.Name);
        Assert.Equal(
            new[] { typeof(AgentExecutionContext), typeof(AgentToolDescriptor) },
            evaluate.GetParameters().Select(parameter => parameter.ParameterType));
    }

    private static AgentExecutionContext CreateContext(
        Guid? companyId,
        string[] capabilities,
        string[] allowedTools) => new(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            companyId,
            AgentChannel.Web,
            new HashSet<string>(capabilities, StringComparer.Ordinal),
            new AgentTaskProfile(
                "support.read",
                1,
                TimeSpan.FromSeconds(1),
                new HashSet<string>(allowedTools, StringComparer.Ordinal)));

    private static AgentToolDescriptor CreateDescriptor(
        AgentToolRisk risk,
        bool requiresExplicitCompany)
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });

        return new AgentToolDescriptor(
            "paguai.read",
            "1.0",
            schema,
            schema,
            "paguai.read",
            risk,
            requiresExplicitCompany,
            false,
            new AgentMcpAnnotations(
                ReadOnlyHint: risk == AgentToolRisk.ReadOnly,
                DestructiveHint: risk != AgentToolRisk.ReadOnly,
                IdempotentHint: true,
                OpenWorldHint: false));
    }
}
