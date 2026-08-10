using System.Xml.Linq;
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

    [Fact]
    public void Ai_project_references_only_foundation()
    {
        var repositoryRoot = RepositoryRoot();
        var aiProjectPath = Path.Combine(
            repositoryRoot,
            "src/Modules/AI/Vinx.Erp.AI/Vinx.Erp.AI.csproj");
        var aiProjectDirectory = Path.GetDirectoryName(aiProjectPath)
            ?? throw new InvalidOperationException("AI project directory could not be resolved.");

        var projectReferences = XDocument.Load(aiProjectPath)
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFullPath(include!, aiProjectDirectory))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var expected = Path.GetFullPath(
            Path.Combine(repositoryRoot, "src/Foundation/Vinx.Erp.Foundation/Vinx.Erp.Foundation.csproj"));

        Assert.Equal([expected], projectReferences);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vinx.Erp.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate Vinx.Erp.sln.");
    }
}
