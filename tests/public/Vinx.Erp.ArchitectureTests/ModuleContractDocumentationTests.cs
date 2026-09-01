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

    [Fact]
    public void Module_contracts_contain_required_headings()
    {
        // A lista de contratos nasce da árvore src/Modules: um módulo novo é
        // coberto automaticamente (foundation e o template entram à parte).
        var relativePaths = DiscoverModuleNames()
            .Select(name => $"docs/modules/{name["Vinx.Erp.".Length..].ToLowerInvariant()}.md")
            .Append("docs/modules/foundation.md")
            .Append("docs/modules/_template.md")
            .ToArray();

        Assert.NotEmpty(DiscoverModuleNames());
        Assert.All(
            relativePaths,
            relativePath =>
            {
                var text = File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));
                Assert.All(RequiredHeadings, heading => Assert.Contains(heading, text, StringComparison.Ordinal));
            });
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

    [Fact]
    public void LegacyInventoryUsesStableReviewSections()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs/inventory/legacy-foundation-inventory.md"));

        foreach (var heading in new[]
        {
            "## Tables",
            "## Views",
            "## Functions",
            "## Policies",
            "## Buckets",
            "## Routed screens",
            "### Services",
            "### Edge Functions",
            "### Direct integrations"
        })
        {
            Assert.Contains(heading, text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("src/lib/frasesMotivacionais.ts", text, StringComparison.Ordinal);
    }

    private static string[] DiscoverModuleNames() =>
        Directory
            .GetDirectories(Path.Combine(RepositoryRoot(), "src", "Modules"))
            .Select(directory => new DirectoryInfo(directory).Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

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
