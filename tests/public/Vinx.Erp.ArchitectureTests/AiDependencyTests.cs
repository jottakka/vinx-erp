using AgentHarnessType = Vinx.Erp.AI.AgentHarness.AgentHarness;
using Xunit;

namespace Vinx.Erp.ArchitectureTests;

public sealed class AiDependencyTests
{
    [Fact]
    public void Ai_module_does_not_depend_on_transport_hosts()
    {
        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "Vinx.Erp.ApiHost",
            "Vinx.Erp.McpHost",
            "Vinx.Erp.WorkerHost"
        };

        var dependencies = typeof(AgentHarnessType).Assembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(dependencies.Intersect(forbidden));
    }
}
