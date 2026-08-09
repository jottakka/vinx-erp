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

    [Fact]
    public void LegacyInventoryRecordsFullDirectIntegrationPaths()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs/inventory/legacy-foundation-inventory.md"));
        var paths = new[]
        {
            "src/components/chamada/ChamadaGlobal.tsx:2",
            "src/feature-module/Authentication/login/login.tsx:6",
            "src/feature-module/Authentication/forgot-password/forgotPassword.tsx:6",
            "src/feature-module/Pages/application-module/chat/chat.tsx:2",
            "src/feature-module/Pages/application-module/chat/calls/callHistory.tsx:3",
            "src/routes/feature.tsx:9"
        };

        foreach (var path in paths)
        {
            Assert.Contains($"`direct Supabase integration {path}` | `{path}`", text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("`direct Supabase integration ChamadaGlobal.tsx:2`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("`direct Supabase integration login.tsx:6`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("`direct Supabase integration forgotPassword.tsx:6`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("`direct Supabase integration chat.tsx:2`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("`direct Supabase integration callHistory.tsx:3`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("`direct Supabase integration routes/feature.tsx:9`", text, StringComparison.Ordinal);
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
