using System.Diagnostics;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Executa o entrypoint público de deploy do storage Wolverine de ponta a ponta,
/// garantindo que o caminho documentado (db-apply, grants, db-assert) funciona
/// com a connection string Npgsql real do repositório.
/// </summary>
public sealed class WolverineStorageScriptTests
{
    [Fact]
    public async Task Setup_script_applies_wolverine_storage_and_runtime_grants()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        var root = WolverineStorageDeployment.FindRepositoryRoot();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("bash")
            {
                WorkingDirectory = root,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("scripts/setup-wolverine-storage.sh");
        process.StartInfo.Environment["FOUNDATION_MIGRATOR_CONNECTION"] =
            fixture.MigratorConnectionString;
        process.StartInfo.Environment["SUPABASE_STORAGE_URL"] = "http://127.0.0.1:54321";
        process.StartInfo.Environment["SUPABASE_STORAGE_SERVICE_ROLE_KEY"] =
            "test-service-role-key";
        process.StartInfo.Environment["SUPABASE_AUTH_ISSUER"] =
            "https://project.supabase.co/auth/v1";
        process.StartInfo.Environment["SUPABASE_AUTH_AUDIENCE"] = "authenticated";
        process.StartInfo.Environment["SUPABASE_AUTH_PUBLISHABLE_KEY"] =
            "test-publishable-key";
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            await Task.WhenAll(standardOutput, standardError);
            Assert.Fail(
                $"setup-wolverine-storage.sh failed with exit code {process.ExitCode}: {await standardError}"
            );
        }

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var grantCheck = new NpgsqlCommand(
            """
            SELECT
                has_schema_privilege('vinx_runtime', 'vinx_wolverine', 'USAGE'),
                to_regclass('vinx_wolverine_queues.wolverine_queue_telegram_inbound') IS NOT NULL,
                to_regclass('vinx_wolverine_queues.wolverine_queue_telegram_inbound_scheduled') IS NOT NULL,
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound',
                    'SELECT'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound',
                    'INSERT'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound',
                    'UPDATE'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound',
                    'DELETE'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound_scheduled',
                    'SELECT'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound_scheduled',
                    'INSERT'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound_scheduled',
                    'UPDATE'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound_scheduled',
                    'DELETE'
                ),
                to_regclass('vinx_wolverine_queues.wolverine_queue_paguai_events') IS NOT NULL,
                to_regclass('vinx_wolverine_queues.wolverine_queue_paguai_events_scheduled') IS NOT NULL,
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_paguai_events',
                    'SELECT, INSERT, UPDATE, DELETE'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_paguai_events_scheduled',
                    'SELECT, INSERT, UPDATE, DELETE'
                ),
                has_function_privilege(
                    'vinx_worker',
                    'notifications.enqueue_paguai_draft_created_notification(uuid,uuid,uuid,uuid)',
                    'EXECUTE'
                )
            """,
            administrator
        );
        await using var reader = await grantCheck.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        for (var index = 3; index <= 15; index++)
        {
            Assert.True(reader.GetBoolean(index));
        }
    }
}
