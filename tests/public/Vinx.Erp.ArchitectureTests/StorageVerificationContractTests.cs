using System.Diagnostics;
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

    [Fact]
    public async Task Storage_verifier_uses_the_default_Docker_context_on_Linux()
    {
        var result = await RunVerifierAsync("Linux");

        Assert.True(result.ExitCode == 0, result.Diagnostics);
        Assert.All(result.SupabaseContexts, context => Assert.Equal("<default>", context));
    }

    [Fact]
    public async Task Storage_verifier_honors_an_explicit_Docker_context()
    {
        var result = await RunVerifierAsync("Linux", dockerContext: "ci-docker");

        Assert.True(result.ExitCode == 0, result.Diagnostics);
        Assert.All(result.SupabaseContexts, context => Assert.Equal("ci-docker", context));
    }

    [Fact]
    public async Task Storage_verifier_prefers_an_available_Docker_Desktop_context_on_macos()
    {
        var result = await RunVerifierAsync("Darwin");

        Assert.True(result.ExitCode == 0, result.Diagnostics);
        Assert.All(result.SupabaseContexts, context => Assert.Equal("desktop-linux", context));
    }

    [Fact]
    public async Task Storage_verifier_uses_the_default_context_when_Docker_Desktop_is_unavailable_on_macos()
    {
        var result = await RunVerifierAsync("Darwin", dockerDesktopContextAvailable: false);

        Assert.True(result.ExitCode == 0, result.Diagnostics);
        Assert.All(result.SupabaseContexts, context => Assert.Equal("<default>", context));
    }

    [Fact]
    public async Task Storage_verifier_reports_a_safe_diagnostic_when_Supabase_start_fails()
    {
        var result = await RunVerifierAsync("Linux", failSupabaseStart: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Supabase local startup failed", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("test-service-role-secret", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("raw Supabase failure", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Storage_verifier_refuses_a_preexisting_stack_without_starting_or_stopping_it()
    {
        var result = await RunVerifierAsync("Linux", preexistingStack: true);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("already running", result.Output, StringComparison.Ordinal);
        Assert.Equal(["supabase status"], result.SupabaseCommands);
    }

    private static async Task<VerifierResult> RunVerifierAsync(
        string operatingSystem,
        string? dockerContext = null,
        bool dockerDesktopContextAvailable = true,
        bool failSupabaseStart = false,
        bool preexistingStack = false)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"vinx-storage-verifier-{Guid.NewGuid():N}");
        var binDirectory = Path.Combine(temporaryDirectory, "bin");
        var logPath = Path.Combine(temporaryDirectory, "supabase-contexts.log");
        var commandLogPath = Path.Combine(temporaryDirectory, "supabase-commands.log");
        Directory.CreateDirectory(binDirectory);

        try
        {
            WriteExecutable(binDirectory, "npm", "#!/usr/bin/env bash\nexit 0\n");
            WriteExecutable(
                binDirectory,
                "npx",
                """
                #!/usr/bin/env bash
                set -euo pipefail
                printf '%s\n' "${DOCKER_CONTEXT-<default>}" >> "${STORAGE_VERIFIER_LOG:?}"
                printf '%s\n' "$*" >> "${STORAGE_VERIFIER_COMMAND_LOG:?}"
                if [[ "${1-} ${2-}" == "supabase start" && "${FAIL_SUPABASE_START-}" == "1" ]]; then
                  printf '%s\n' 'raw Supabase failure: test-service-role-secret' >&2
                  exit 1
                fi
                if [[ "${1-} ${2-} ${3-}" == "supabase status -o" && "${4-}" == "env" ]]; then
                  printf '%s\n' 'API_URL="http://127.0.0.1:54321"'
                  printf '%s\n' 'SERVICE_ROLE_KEY="test-service-role-secret"'
                  exit 0
                fi
                if [[ "${1-} ${2-}" == "supabase status" ]]; then
                  [[ "${PREEXISTING_SUPABASE_STACK-}" == "1" ]] && exit 0
                  exit 1
                fi
                """);
            WriteExecutable(binDirectory, "dotnet", "#!/usr/bin/env bash\nexit 0\n");
            WriteExecutable(
                binDirectory,
                "docker",
                """
                #!/usr/bin/env bash
                if [[ "${DOCKER_DESKTOP_CONTEXT_AVAILABLE-}" == "1" && "$1 $2 $3" == "context inspect desktop-linux" ]]; then
                  exit 0
                fi
                exit 1
                """);
            WriteExecutable(binDirectory, "uname", $"#!/usr/bin/env bash\nprintf '%s\\n' '{operatingSystem}'\n");

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = "scripts/verify-supabase-storage.sh",
                WorkingDirectory = RepositoryRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            process.StartInfo.Environment["PATH"] = $"{binDirectory}{Path.PathSeparator}{process.StartInfo.Environment["PATH"]}";
            process.StartInfo.Environment["STORAGE_VERIFIER_LOG"] = logPath;
            process.StartInfo.Environment["STORAGE_VERIFIER_COMMAND_LOG"] = commandLogPath;
            process.StartInfo.Environment["DOCKER_DESKTOP_CONTEXT_AVAILABLE"] = dockerDesktopContextAvailable ? "1" : "0";
            process.StartInfo.Environment.Remove("DOCKER_CONTEXT");
            process.StartInfo.Environment.Remove("FAIL_SUPABASE_START");
            process.StartInfo.Environment.Remove("PREEXISTING_SUPABASE_STACK");
            if (dockerContext is not null)
            {
                process.StartInfo.Environment["DOCKER_CONTEXT"] = dockerContext;
            }

            if (failSupabaseStart)
            {
                process.StartInfo.Environment["FAIL_SUPABASE_START"] = "1";
            }

            if (preexistingStack)
            {
                process.StartInfo.Environment["PREEXISTING_SUPABASE_STACK"] = "1";
            }

            process.Start();
            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await standardOutputTask + await standardErrorTask;
            var contexts = File.Exists(logPath)
                ? await File.ReadAllLinesAsync(logPath)
                : [];
            var commands = File.Exists(commandLogPath)
                ? await File.ReadAllLinesAsync(commandLogPath)
                : [];

            return new VerifierResult(process.ExitCode, output, contexts, commands);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static void WriteExecutable(string directory, string fileName, string contents)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Storage verifier contract tests require a Unix shell.");
        }

        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute);
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

    private sealed record VerifierResult(
        int ExitCode,
        string Output,
        string[] SupabaseContexts,
        string[] SupabaseCommands)
    {
        public string Diagnostics => $"Output: {Output}; contexts: {string.Join(",", SupabaseContexts)}";
    }
}
