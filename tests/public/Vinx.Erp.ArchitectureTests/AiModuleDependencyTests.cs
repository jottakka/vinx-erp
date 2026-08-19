using Xunit;

namespace Vinx.Erp.ArchitectureTests;

public sealed class AiModuleDependencyTests
{
    [Fact]
    public void Foundation_references_are_unchanged() // AI não entra na Foundation
    {
        var foundation = ReadProject("src/Foundation/Vinx.Erp.Foundation/Vinx.Erp.Foundation.csproj");
        Assert.DoesNotContain("Vinx.Erp.AI", foundation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/Modules/Vinx.Erp.Paguai/Vinx.Erp.Paguai.csproj")]
    [InlineData("src/Modules/Vinx.Erp.Atendimento/Vinx.Erp.Atendimento.csproj")]
    public void Domain_modules_do_not_reference_ai(string projectPath)
    {
        Assert.DoesNotContain("Vinx.Erp.AI.csproj", ReadProject(projectPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_module_references_foundation_and_no_hosts()
    {
        var ai = ReadProject("src/Modules/Vinx.Erp.AI/Vinx.Erp.AI.csproj");
        Assert.Contains("Vinx.Erp.Foundation.csproj", ai, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiHost", ai, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkerHost", ai, StringComparison.Ordinal);
        Assert.DoesNotContain("McpHost", ai, StringComparison.Ordinal);
    }

    private static string ReadProject(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));

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
