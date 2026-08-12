using System.Text.Json;
using Vinx.Erp.AI.AgentHarness;
using Vinx.Erp.AI.Tools;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class AgentToolRegistryTests
{
    [Fact]
    public void Registry_rejects_duplicate_tool_name_and_version()
    {
        var first = new StubTool(CreateDescriptor("payments.read", "1.0.0"));
        var duplicate = new StubTool(CreateDescriptor("payments.read", "1.0.0"));

        Assert.Throws<ArgumentException>(() => new AgentToolRegistry([first, duplicate]));
    }

    [Fact]
    public void Registry_rejects_multiple_active_versions_for_same_name_ordinally()
    {
        var first = new StubTool(CreateDescriptor("payments.read", "1.0.0"));
        var newer = new StubTool(CreateDescriptor("payments.read", "2.0.0"));
        var differentlyCased = new StubTool(CreateDescriptor("PAYMENTS.READ", "1.0.0"));

        Assert.Throws<ArgumentException>(() => new AgentToolRegistry([first, newer]));
        var registry = new AgentToolRegistry([first, differentlyCased]);

        Assert.Equal(2, registry.Descriptors.Count);
    }

    [Fact]
    public void Registry_keeps_immutable_descriptor_snapshot_and_resolves_by_name()
    {
        var tool = new StubTool(CreateDescriptor("payments.read", "1.0.0"));
        var tools = new List<IAgentTool> { tool };

        var registry = new AgentToolRegistry(tools);
        tools.Clear();
        tool.Descriptor = CreateDescriptor("payments.read", "2.0.0");

        Assert.Single(registry.Descriptors);
        Assert.True(registry.TryResolve(CreateCall("payments.read", "{}"), out var registration));
        Assert.Equal("payments.read", registration.Descriptor.Name);
        Assert.Equal("1.0.0", registration.Descriptor.Version);
        Assert.False(registry.TryResolve(CreateCall("Payments.Read", "{}"), out _));
    }

    [Fact]
    public void Descriptor_and_call_own_json_after_source_document_is_disposed()
    {
        AgentToolDescriptor descriptor;
        AgentToolCall call;

        using (var schema = JsonDocument.Parse("""{"type":"object","properties":{"amount":{"type":"number"}}}"""))
        using (var output = JsonDocument.Parse("""{"type":"object"}"""))
        using (var payload = JsonDocument.Parse("""{"amount":12}"""))
        {
            descriptor = new AgentToolDescriptor(
                "payments.read",
                "1.0.0",
                schema.RootElement,
                output.RootElement,
                "payments.read",
                AgentToolRisk.ReadOnly,
                requiresExplicitCompany: false,
                requiresIdempotency: false,
                new AgentToolMcpHints("Read payments", readOnlyHint: true));
            call = new AgentToolCall("payments.read", payload.RootElement);
        }

        Assert.Contains("amount", descriptor.InputSchema.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("{\"amount\":12}", call.Arguments.GetRawText());
    }

    [Fact]
    public void Call_removes_company_aliases_at_every_depth_case_insensitively()
    {
        var call = CreateCall(
            "payments.read",
            """{"empresa_id":"forged","safe":{"empresaId":"forged","items":[{"company_id":"forged"},{"COMPANYID":"forged","nested":{"Empresa_Id":"forged","ok":true}}]},"companyId":"forged"}""");

        var arguments = call.Arguments.GetRawText();

        Assert.DoesNotContain("empresa_id", arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("empresaId", arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("company_id", arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("companyId", arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"ok\":true", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_only_descriptor_rejects_mcp_write_or_destructive_hints()
    {
        Assert.Throws<ArgumentException>(() => CreateDescriptor(
            "payments.read",
            "1.0.0",
            readOnlyHint: false));
        Assert.Throws<ArgumentException>(() => CreateDescriptor(
            "payments.read",
            "1.0.0",
            destructiveHint: true));
    }

    [Fact]
    public void Write_descriptor_rejects_read_only_hint_or_required_idempotency_mismatch()
    {
        Assert.Throws<ArgumentException>(() => CreateDescriptor(
            "payments.write",
            "1.0.0",
            risk: AgentToolRisk.InternalWrite,
            requiresIdempotency: false,
            readOnlyHint: true));
        Assert.Throws<ArgumentException>(() => CreateDescriptor(
            "payments.write",
            "1.0.0",
            risk: AgentToolRisk.InternalWrite,
            requiresIdempotency: true,
            readOnlyHint: true,
            idempotentHint: true));
        Assert.Throws<ArgumentException>(() => CreateDescriptor(
            "payments.write",
            "1.0.0",
            risk: AgentToolRisk.InternalWrite,
            requiresIdempotency: true,
            readOnlyHint: false,
            idempotentHint: false));
    }

    [Fact]
    public void Non_idempotent_destructive_external_write_descriptor_can_be_consistent()
    {
        var descriptor = CreateDescriptor(
            "payments.submit",
            "1.0.0",
            risk: AgentToolRisk.ExternalWrite,
            requiresIdempotency: false,
            readOnlyHint: false,
            destructiveHint: true,
            idempotentHint: false);

        Assert.Equal(AgentToolRisk.ExternalWrite, descriptor.Risk);
        Assert.True(descriptor.McpHints.DestructiveHint);
        Assert.False(descriptor.RequiresIdempotency);
    }

    private static AgentToolDescriptor CreateDescriptor(
        string name,
        string version,
        AgentToolRisk risk = AgentToolRisk.ReadOnly,
        bool requiresIdempotency = false,
        bool? readOnlyHint = null,
        bool destructiveHint = false,
        bool idempotentHint = false)
    {
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        using var output = JsonDocument.Parse("""{"type":"object"}""");

        return new AgentToolDescriptor(
            name,
            version,
            schema.RootElement,
            output.RootElement,
            "payments.read",
            risk,
            requiresExplicitCompany: false,
            requiresIdempotency,
            new AgentToolMcpHints(
                "Read payments",
                readOnlyHint ?? risk == AgentToolRisk.ReadOnly,
                destructiveHint,
                idempotentHint));
    }

    private static AgentToolCall CreateCall(string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new AgentToolCall(name, document.RootElement);
    }

    private sealed class StubTool(AgentToolDescriptor descriptor) : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; set; } = descriptor;

        public AgentToolValidationResult Validate(AgentToolCall call) => AgentToolValidationResult.Valid();

        public Task<AgentToolResult> InvokeAsync(AgentToolInvocation invocation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
