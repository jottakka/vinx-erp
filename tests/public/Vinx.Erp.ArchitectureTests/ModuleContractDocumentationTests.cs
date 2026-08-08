using Xunit;

namespace Vinx.Erp.ArchitectureTests;

public sealed class ModuleContractDocumentationTests
{
    private static readonly string[] RequiredHeadings =
    [
        "## Ownership",
        "## Company scope",
        "## Contract version",
        "## Deletion strategy",
        "## Migration notes"
    ];

    [Theory]
    [InlineData("docs/modules/foundation.md")]
    [InlineData("docs/modules/_template.md")]
    public void Module_contract_contains_required_headings(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));
        Assert.All(RequiredHeadings, heading => Assert.Contains(heading, text, StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyInventoryDeclaresCompletion()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs/inventory/legacy-foundation-inventory.md"));
        Assert.Contains(
            "Inventory completion: every discovered table, view, function, policy, bucket, routed screen and Supabase-backed feature has a classified row.",
            text,
            StringComparison.Ordinal);
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
