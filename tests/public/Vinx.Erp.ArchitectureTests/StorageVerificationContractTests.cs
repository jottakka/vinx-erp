using Xunit;

namespace Vinx.Erp.ArchitectureTests;

public sealed class StorageVerificationContractTests
{
    [Fact]
    public void Storage_ci_job_depends_on_public_verification()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github/workflows/ci.yml"));
        var storageJobStart = workflow.IndexOf("\n  verify-storage:", StringComparison.Ordinal);

        Assert.True(storageJobStart >= 0, "Storage verification job was not found.");
        Assert.Contains("needs: verify-public", workflow[storageJobStart..], StringComparison.Ordinal);
    }

    [Fact]
    public void Storage_verifier_retains_only_allowlisted_status_values_and_clears_them_on_exit()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts/verify-supabase-storage.sh"));

        Assert.DoesNotContain("supabase_status", script, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(script, "supabase status -o env"));
        Assert.Contains("sed -n 's/^API_URL=//p'", script, StringComparison.Ordinal);
        Assert.Contains("sed -n 's/^SERVICE_ROLE_KEY=//p'", script, StringComparison.Ordinal);
        Assert.Contains(
            "unset api_url service_role_key SUPABASE_STORAGE_URL SUPABASE_STORAGE_SERVICE_ROLE_KEY",
            script,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = text.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
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
