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
    public void Run_request_exposes_only_untrusted_message_and_external_context()
    {
        var propertyNames = typeof(AgentRunRequest)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "ExternalContext", "UserMessage" }, propertyNames);
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
    public void Execution_context_rejects_empty_actor_user_id()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateContext(new ActorContext(Guid.Empty, Guid.NewGuid())));
    }

    [Fact]
    public void Execution_context_rejects_empty_actor_correlation_id()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateContext(new ActorContext(Guid.NewGuid(), Guid.Empty)));
    }

    [Fact]
    public void Context_item_rejects_nan_confidence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentContextItem("normalized", "safe", double.NaN));
    }

    [Fact]
    public void Task_profile_rejects_non_positive_timeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTaskProfile(
            "support.read", 1, TimeSpan.Zero, new HashSet<string>()));
    }

    [Fact]
    public void Task_profile_rejects_timeout_above_portable_cancel_after_limit()
    {
        var timeout = TimeSpan.FromMilliseconds(int.MaxValue) + TimeSpan.FromTicks(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTaskProfile(
            "support.read", 1, timeout, new HashSet<string>()));
    }

    [Fact]
    public void Task_profile_accepts_portable_cancel_after_limit()
    {
        var timeout = TimeSpan.FromMilliseconds(int.MaxValue);

        var profile = new AgentTaskProfile(
            "support.read", 1, timeout, new HashSet<string>());

        Assert.Equal(timeout, profile.Timeout);
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

    [Theory]
    [InlineData("EmPrEsA_Id")]
    [InlineData("eMpReSaId")]
    [InlineData("CoMpAnY_iD")]
    [InlineData("cOmPaNyId")]
    public void Tool_call_removes_top_level_company_scope_alias_case_insensitively(string alias)
    {
        using var arguments = JsonDocument.Parse($$"""
            {
              "{{alias}}": "model-supplied-company",
              "query": "open invoices"
            }
            """);

        var call = new AgentToolCall("paguai.read", arguments.RootElement);

        Assert.False(call.Arguments.TryGetProperty(alias, out _));
        Assert.Equal("open invoices", call.Arguments.GetProperty("query").GetString());
    }

    [Fact]
    public void Tool_call_removes_nested_company_scope_aliases_and_preserves_business_fields()
    {
        using var arguments = JsonDocument.Parse("""
            {
              "filter": {
                "empresa_id": "business-value-1",
                "status": "open",
                "details": {
                  "empresaId": "business-value-2",
                  "documentNumber": "INV-42"
                },
                "items": [
                  {
                    "company_id": "business-value-3",
                    "sku": "SKU-1"
                  },
                  {
                    "metadata": {
                      "companyId": "business-value-4",
                      "quantity": 2
                    }
                  }
                ]
              }
            }
            """);

        var call = new AgentToolCall("paguai.read", arguments.RootElement);
        var filter = call.Arguments.GetProperty("filter");
        var details = filter.GetProperty("details");
        var items = filter.GetProperty("items");
        var firstItem = items[0];
        var metadata = items[1].GetProperty("metadata");

        Assert.False(filter.TryGetProperty("empresa_id", out _));
        Assert.Equal("open", filter.GetProperty("status").GetString());
        Assert.False(details.TryGetProperty("empresaId", out _));
        Assert.Equal("INV-42", details.GetProperty("documentNumber").GetString());
        Assert.False(firstItem.TryGetProperty("company_id", out _));
        Assert.Equal("SKU-1", firstItem.GetProperty("sku").GetString());
        Assert.False(metadata.TryGetProperty("companyId", out _));
        Assert.Equal(2, metadata.GetProperty("quantity").GetInt32());
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

    private static AgentExecutionContext CreateContext(Guid? companyId) => CreateContext(
        new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
        companyId);

    private static AgentExecutionContext CreateContext(ActorContext actor) => CreateContext(actor, null);

    private static AgentExecutionContext CreateContext(ActorContext actor, Guid? companyId) => new(
        actor,
        companyId,
        AgentChannel.Web,
        new HashSet<string>(StringComparer.Ordinal) { "paguai.read" },
        new AgentTaskProfile(
            "support.read",
            1,
            TimeSpan.FromSeconds(1),
            new HashSet<string>(StringComparer.Ordinal) { "paguai.read" }));
}
