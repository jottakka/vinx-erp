using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.ArchitectureTests;

public sealed class FoundationDependencyTests
{
    [Fact]
    public void Foundation_does_not_depend_on_transport_hosts()
    {
        var forbiddenHosts = new HashSet<string>(StringComparer.Ordinal)
        {
            "Vinx.Erp.ApiHost",
            "Vinx.Erp.McpHost",
            "Vinx.Erp.WorkerHost"
        };

        var dependencies = typeof(FoundationDbContext).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(dependencies.Intersect(forbiddenHosts));
    }
}
