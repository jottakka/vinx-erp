using System.Diagnostics;
using System.Text.Json;
using Vinx.Erp.AI.AgentHarness;
using Xunit;

namespace Vinx.Erp.ArchitectureTests;

public sealed class AiDependencyTests
{
    [Fact]
    public void Ai_module_does_not_depend_on_transport_hosts()
    {
        var forbiddenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Vinx.Erp.ApiHost",
            "Vinx.Erp.McpHost",
            "Vinx.Erp.WorkerHost"
        };

        var dependencies = typeof(AgentHarnessRunner).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Empty(dependencies.Intersect(forbiddenHosts, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ai_project_does_not_reference_transport_host_projects()
    {
        var forbiddenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Vinx.Erp.ApiHost.csproj",
            "Vinx.Erp.McpHost.csproj",
            "Vinx.Erp.WorkerHost.csproj"
        };
        var projectPath = Path.GetFullPath(Path.Combine(
            RepositoryRoot(),
            "src",
            "Modules",
            "AI",
            "Vinx.Erp.AI",
            "Vinx.Erp.AI.csproj"));
        var projectReferences = FindProjectGraph(projectPath)
            .Select(Path.GetFileName);

        Assert.Empty(projectReferences.Intersect(forbiddenProjects, StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyCollection<string> FindProjectGraph(string rootProjectPath)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        pending.Enqueue(Path.GetFullPath(rootProjectPath));

        while (pending.TryDequeue(out var projectPath))
        {
            if (!visited.Add(projectPath))
            {
                continue;
            }

            foreach (var referencedProject in GetEvaluatedProjectReferences(projectPath))
            {
                pending.Enqueue(referencedProject);
            }
        }

        return visited;
    }

    private static IReadOnlyCollection<string> GetEvaluatedProjectReferences(string projectPath)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot(),
            ArgumentList =
            {
                "msbuild",
                projectPath,
                "-nologo",
                "-getItem:ProjectReference"
            }
        }) ?? throw new InvalidOperationException("Could not start MSBuild project-reference evaluation.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"MSBuild project-reference evaluation failed with exit code {process.ExitCode}.");
        }

        using var document = JsonDocument.Parse(standardOutput.Result);
        if (!document.RootElement.TryGetProperty("Items", out var items) ||
            !items.TryGetProperty("ProjectReference", out var projectReferences) ||
            projectReferences.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return projectReferences.EnumerateArray()
            .Select(projectReference => projectReference.TryGetProperty("FullPath", out var fullPath)
                ? fullPath.GetString()
                : null)
            .Where(fullPath => !string.IsNullOrWhiteSpace(fullPath))
            .Select(fullPath => Path.GetFullPath(fullPath!))
            .ToArray();
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
