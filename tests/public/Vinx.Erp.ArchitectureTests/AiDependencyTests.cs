using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Vinx.Erp.AI.AgentHarness;
using Xunit;

namespace Vinx.Erp.ArchitectureTests;

public sealed class AiDependencyTests
{
    private static readonly TimeSpan MsbuildEvaluationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MsbuildInterruptionTimeout = TimeSpan.FromSeconds(5);

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

    [Fact]
    public void Project_graph_preserves_ProjectReference_global_property_precedence()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"vinx-ai-dependency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            var rootProjectPath = WriteProject(
                temporaryDirectory,
                "Root",
                "Root.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="../PropertiesChild/PropertiesChild.csproj"
                                      Properties="TargetFramework=net10.0;IncludeMcpHost=false"
                                      SetTargetFramework="TargetFramework=net8.0"
                                      AdditionalProperties="IncludeMcpHost=true" />
                    <ProjectReference Include="../RemovalChild/RemovalChild.csproj"
                                      Properties="RemovalRoute=enabled;UndefineHost=true;GlobalRemoveHost=true"
                                      UndefineProperties="UndefineHost"
                                      GlobalPropertiesToRemove="GlobalRemoveHost" />
                    <ProjectReference Include="../SetChild/SetChild.csproj"
                                      SetConfiguration="Configuration=Release"
                                      SetPlatform="Platform=x64"
                                      SetTargetFramework="TargetFramework=net10.0" />
                  </ItemGroup>
                </Project>
                """);
            var apiHostProjectPath = WriteProject(
                temporaryDirectory,
                "Vinx.Erp.ApiHost",
                "Vinx.Erp.ApiHost.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk" />
                """);
            var mcpHostProjectPath = WriteProject(
                temporaryDirectory,
                "Vinx.Erp.McpHost",
                "Vinx.Erp.McpHost.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk" />
                """);
            var workerHostProjectPath = WriteProject(
                temporaryDirectory,
                "Vinx.Erp.WorkerHost",
                "Vinx.Erp.WorkerHost.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk" />
                """);
            WriteProject(
                temporaryDirectory,
                "PropertiesChild",
                "PropertiesChild.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Vinx.Erp.McpHost/Vinx.Erp.McpHost.csproj"
                                      Condition="'$(TargetFramework)' == 'net10.0' and '$(IncludeMcpHost)' == 'true'" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(
                temporaryDirectory,
                "RemovalChild",
                "RemovalChild.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="../Vinx.Erp.ApiHost/Vinx.Erp.ApiHost.csproj"
                                      Condition="'$(RemovalRoute)' == 'enabled' and '$(UndefineHost)' == '' and '$(GlobalRemoveHost)' == ''" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(
                temporaryDirectory,
                "SetChild",
                "SetChild.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Configuration>Debug</Configuration>
                    <Platform>AnyCPU</Platform>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Vinx.Erp.WorkerHost/Vinx.Erp.WorkerHost.csproj"
                                      Condition="'$(TargetFramework)' == 'net10.0' and '$(Configuration)' == 'Release' and '$(Platform)' == 'x64'" />
                  </ItemGroup>
                </Project>
                """);

            var projectGraph = FindProjectGraph(rootProjectPath);

            Assert.Contains(apiHostProjectPath, projectGraph, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(mcpHostProjectPath, projectGraph, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(workerHostProjectPath, projectGraph, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Items":{}}""")]
    [InlineData("""{"Items":{"ProjectReference":{}}}""")]
    [InlineData("""{"Items":{"ProjectReference":[{}]}}""")]
    public void Project_graph_rejects_missing_or_malformed_reference_collection(string standardOutput)
    {
        Assert.Throws<InvalidOperationException>(() => ParseProjectReferences(
            standardOutput,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Project_graph_accepts_explicit_empty_reference_collection()
    {
        var projectReferences = ParseProjectReferences(
            """{"Items":{"ProjectReference":[]}}""",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.Empty(projectReferences);
    }

    private static IReadOnlyCollection<string> FindProjectGraph(string rootProjectPath)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<ProjectEvaluationContext>();
        pending.Enqueue(new ProjectEvaluationContext(
            Path.GetFullPath(rootProjectPath),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

        while (pending.TryDequeue(out var context))
        {
            if (!visited.Add(context.VisitKey))
            {
                continue;
            }

            projects.Add(context.ProjectPath);

            foreach (var referencedProject in GetEvaluatedProjectReferences(context))
            {
                pending.Enqueue(referencedProject);
            }
        }

        return projects;
    }

    private static IReadOnlyCollection<ProjectEvaluationContext> GetEvaluatedProjectReferences(
        ProjectEvaluationContext context)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot(),
            ArgumentList =
            {
                "msbuild",
                context.ProjectPath,
                "-nologo",
                "-getItem:ProjectReference"
            }
        };

        foreach (var globalProperty in context.GlobalProperties)
        {
            startInfo.ArgumentList.Add($"-p:{globalProperty.Key}={globalProperty.Value}");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start MSBuild project-reference evaluation.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(MsbuildEvaluationTimeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // MSBuild completed while the timeout was being handled.
            }

            var exitedAfterInterruption = process.WaitForExit(MsbuildInterruptionTimeout);
            var outputDrainedAfterInterruption = WaitForOutput(
                standardOutput,
                standardError,
                MsbuildInterruptionTimeout);

            if (!exitedAfterInterruption || !outputDrainedAfterInterruption)
            {
                throw new InvalidOperationException("MSBuild project-reference evaluation did not stop within the interruption timeout.");
            }

            throw new InvalidOperationException("MSBuild project-reference evaluation timed out.");
        }

        if (!WaitForOutput(standardOutput, standardError, MsbuildEvaluationTimeout))
        {
            throw new InvalidOperationException("MSBuild project-reference evaluation output did not complete within the timeout.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"MSBuild project-reference evaluation failed with exit code {process.ExitCode}.");
        }

        return ParseProjectReferences(standardOutput.Result, context.GlobalProperties);
    }

    private static IReadOnlyCollection<ProjectEvaluationContext> ParseProjectReferences(
        string standardOutput,
        IReadOnlyDictionary<string, string> parentGlobalProperties)
    {
        using var document = JsonDocument.Parse(standardOutput);
        if (!document.RootElement.TryGetProperty("Items", out var items) ||
            !items.TryGetProperty("ProjectReference", out var projectReferences) ||
            projectReferences.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "MSBuild project-reference evaluation did not contain an Items.ProjectReference array.");
        }

        return projectReferences.EnumerateArray()
            .Select(projectReference => CreateProjectEvaluationContext(projectReference, parentGlobalProperties))
            .ToArray();
    }

    private static ProjectEvaluationContext CreateProjectEvaluationContext(
        JsonElement projectReference,
        IReadOnlyDictionary<string, string> parentGlobalProperties)
    {
        if (projectReference.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("MSBuild project-reference evaluation contained a non-object reference.");
        }

        var fullPath = GetMetadata(projectReference, "FullPath");
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            throw new InvalidOperationException("MSBuild project-reference evaluation contained a reference without FullPath.");
        }

        var globalProperties = new Dictionary<string, string>(
            parentGlobalProperties,
            StringComparer.OrdinalIgnoreCase);

        var properties = GetMetadata(projectReference, "Properties");
        if (!string.IsNullOrEmpty(properties))
        {
            AddGlobalProperties(globalProperties, properties);
        }
        else
        {
            foreach (var metadataName in new[] { "SetConfiguration", "SetPlatform", "SetTargetFramework" })
            {
                AddGlobalProperties(globalProperties, GetMetadata(projectReference, metadataName));
            }
        }

        AddGlobalProperties(globalProperties, GetMetadata(projectReference, "AdditionalProperties"));

        foreach (var metadataName in new[] { "UndefineProperties", "GlobalPropertiesToRemove" })
        {
            RemoveGlobalProperties(globalProperties, GetMetadata(projectReference, metadataName));
        }

        return new ProjectEvaluationContext(Path.GetFullPath(fullPath), globalProperties);
    }

    private static string? GetMetadata(JsonElement projectReference, string metadataName) =>
        projectReference.TryGetProperty(metadataName, out var metadata)
            ? metadata.GetString()
            : null;

    private static void AddGlobalProperties(
        IDictionary<string, string> globalProperties,
        string? propertyAssignments)
    {
        if (string.IsNullOrWhiteSpace(propertyAssignments))
        {
            return;
        }

        foreach (var assignment in propertyAssignments.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = assignment.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidOperationException("Project-reference metadata contained an invalid global property assignment.");
            }

            globalProperties[assignment[..separator]] = assignment[(separator + 1)..];
        }
    }

    private static void RemoveGlobalProperties(IDictionary<string, string> globalProperties, string? propertyNames)
    {
        if (string.IsNullOrWhiteSpace(propertyNames))
        {
            return;
        }

        foreach (var propertyName in propertyNames.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            globalProperties.Remove(propertyName);
        }
    }

    private static bool WaitForOutput(Task standardOutput, Task standardError, TimeSpan timeout)
    {
        try
        {
            return Task.WaitAll([standardOutput, standardError], timeout);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new InvalidOperationException("Could not read MSBuild project-reference evaluation output.", exception);
        }
    }

    private static string WriteProject(
        string temporaryDirectory,
        string projectDirectoryName,
        string projectFileName,
        string contents)
    {
        var projectDirectory = Path.Combine(temporaryDirectory, projectDirectoryName);
        Directory.CreateDirectory(projectDirectory);

        var projectPath = Path.Combine(projectDirectory, projectFileName);
        File.WriteAllText(projectPath, contents);
        return projectPath;
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Could not locate the AI dependency test source directory.");
        var repositoryRoot = Path.GetFullPath(Path.Combine(sourceDirectory, "..", "..", ".."));

        return File.Exists(Path.Combine(repositoryRoot, "Vinx.Erp.sln"))
            ? repositoryRoot
            : throw new InvalidOperationException("Could not locate Vinx.Erp.sln from the AI dependency test source file.");
    }

    private sealed record ProjectEvaluationContext(
        string ProjectPath,
        IReadOnlyDictionary<string, string> GlobalProperties)
    {
        public string VisitKey => JsonSerializer.Serialize(new
        {
            ProjectPath,
            GlobalProperties = GlobalProperties
                .OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase)
                .Select(property => new[] { property.Key, property.Value })
        });
    }
}
