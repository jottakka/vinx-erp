using System.Diagnostics;
using Npgsql;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

internal static class WolverineStorageDeployment
{
    public static async Task ApplyAsync(FoundationPostgresFixture fixture)
    {
        var root = FindRepositoryRoot();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--project");
        process.StartInfo.ArgumentList.Add("src/Hosts/Vinx.Erp.ApiHost");
        process.StartInfo.ArgumentList.Add("--no-build");
        process.StartInfo.ArgumentList.Add("--no-launch-profile");
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add("db-apply");
        process.StartInfo.Environment["FOUNDATION_RUNTIME_CONNECTION"] =
            fixture.MigratorConnectionString;
        process.StartInfo.Environment["SUPABASE_STORAGE_URL"] = "http://127.0.0.1:54321";
        process.StartInfo.Environment["SUPABASE_STORAGE_SERVICE_ROLE_KEY"] =
            "test-service-role-key";
        process.StartInfo.Environment["SUPABASE_AUTH_ISSUER"] =
            "https://project.supabase.co/auth/v1";
        process.StartInfo.Environment["SUPABASE_AUTH_AUDIENCE"] = "authenticated";
        process.StartInfo.Environment["SUPABASE_AUTH_PUBLISHABLE_KEY"] = "test-publishable-key";
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            await Task.WhenAll(standardOutput, standardError);
            throw new InvalidOperationException(
                $"Wolverine deploy setup failed with exit code {process.ExitCode}."
            );
        }

        await Task.WhenAll(standardOutput, standardError);

        await using var migrator = new NpgsqlConnection(fixture.MigratorConnectionString);
        await migrator.OpenAsync();
        var grants = await File.ReadAllTextAsync(
            Path.Combine(root, "infra", "database", "grant-wolverine-runtime.sql")
        );
        await using var command = new NpgsqlCommand(grants, migrator);
        await command.ExecuteNonQueryAsync();

        await RunDeploymentCommandAsync(root, fixture, "db-assert");
    }

    private static async Task RunDeploymentCommandAsync(
        string root,
        FoundationPostgresFixture fixture,
        string commandName
    )
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--project");
        process.StartInfo.ArgumentList.Add("src/Hosts/Vinx.Erp.ApiHost");
        process.StartInfo.ArgumentList.Add("--no-build");
        process.StartInfo.ArgumentList.Add("--no-launch-profile");
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add(commandName);
        process.StartInfo.Environment["FOUNDATION_RUNTIME_CONNECTION"] =
            fixture.MigratorConnectionString;
        process.StartInfo.Environment["SUPABASE_STORAGE_URL"] = "http://127.0.0.1:54321";
        process.StartInfo.Environment["SUPABASE_STORAGE_SERVICE_ROLE_KEY"] =
            "test-service-role-key";
        process.StartInfo.Environment["SUPABASE_AUTH_ISSUER"] =
            "https://project.supabase.co/auth/v1";
        process.StartInfo.Environment["SUPABASE_AUTH_AUDIENCE"] = "authenticated";
        process.StartInfo.Environment["SUPABASE_AUTH_PUBLISHABLE_KEY"] = "test-publishable-key";
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(standardOutput, standardError);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Wolverine deployment {commandName} failed with exit code {process.ExitCode}."
            );
        }
    }

    private static string FindRepositoryRoot()
    {
        for (
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vinx.Erp.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The VINX repository root was not found.");
    }
}
