using System.Text.Json;
using Vinx.Erp.AI.Tools;

namespace Vinx.Erp.AI.UnitTests.Fixtures;

internal sealed class RecordingAgentTool : IAgentTool
{
    public RecordingAgentTool(string name)
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });

        Descriptor = new AgentToolDescriptor(
            name,
            "1.0",
            schema,
            schema,
            "paguai.read",
            AgentToolRisk.ReadOnly,
            true,
            false,
            new AgentMcpAnnotations(true, false, true, false));
    }

    public AgentToolDescriptor Descriptor { get; }

    public AgentToolValidationResult Validate(AgentToolCall call) => new(true, null);

    public Task<AgentToolResult> InvokeAsync(
        AgentToolInvocation invocation,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}
