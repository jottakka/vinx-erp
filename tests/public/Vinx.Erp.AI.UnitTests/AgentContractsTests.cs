using System.Text.Json;
using Vinx.Erp.AI.AgentHarness;
using Vinx.Erp.AI.Models;
using Vinx.Erp.AI.Tools;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class AgentContractsTests
{
    [Fact]
    public void Run_request_rejects_blank_user_message()
    {
        Assert.Throws<ArgumentException>(() => new AgentRunRequest(" ", []));
    }

    [Fact]
    public void Task_profile_rejects_zero_tool_turns()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTaskProfile(
            "support.read", 0, TimeSpan.FromSeconds(10), new HashSet<string>()));
    }

    [Fact]
    public void Execution_context_keeps_server_supplied_company()
    {
        var companyId = Guid.NewGuid();

        Assert.Equal(companyId, CreateContext(companyId).CompanyId);
    }

    [Fact]
    public void Task_profile_rejects_non_positive_timeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTaskProfile(
            "support.read", 1, TimeSpan.Zero, new HashSet<string>()));
    }

    [Fact]
    public void Task_profile_rejects_blank_allowed_tool_name()
    {
        Assert.Throws<ArgumentException>(() => new AgentTaskProfile(
            "support.read", 1, TimeSpan.FromSeconds(10), new HashSet<string> { " " }));
    }

    [Fact]
    public void Tool_descriptor_clones_schema_values()
    {
        AgentToolDescriptor descriptor;

        using (var input = JsonDocument.Parse("""{"type":"object"}"""))
        using (var output = JsonDocument.Parse("""{"type":"object"}"""))
        {
            descriptor = new AgentToolDescriptor(
                "paguai.read",
                "1.0",
                input.RootElement,
                output.RootElement,
                "paguai.read",
                AgentToolRisk.ReadOnly,
                true,
                false,
                new AgentMcpAnnotations(true, false, true, false));
        }

        Assert.Equal("object", descriptor.InputSchema.GetProperty("type").GetString());
        Assert.Equal("object", descriptor.OutputSchema.GetProperty("type").GetString());
    }

    [Fact]
    public void Model_request_exposes_only_untrusted_request_and_approved_model_inputs()
    {
        var propertyNames = typeof(AgentModelRequest)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "AllowedTools", "PriorToolResults", "Profile", "Request" },
            propertyNames);
    }

    [Fact]
    public void Run_event_exposes_only_safe_metadata()
    {
        var propertyNames = typeof(AgentRunEvent)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "ActorId",
                "Channel",
                "CompanyId",
                "CorrelationId",
                "Outcome",
                "Phase",
                "ProfileId",
                "RunId",
                "ToolName",
                "ToolRisk"
            },
            propertyNames);
    }

    private static AgentExecutionContext CreateContext(Guid? companyId) => new(
        new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
        companyId,
        AgentChannel.Web,
        new HashSet<string>(StringComparer.Ordinal) { "paguai.read" },
        new AgentTaskProfile(
            "support.read",
            1,
            TimeSpan.FromSeconds(1),
            new HashSet<string>(StringComparer.Ordinal) { "paguai.read" }));
}
