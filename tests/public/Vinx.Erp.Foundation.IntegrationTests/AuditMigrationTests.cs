using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AuditMigrationTests
{
    private const string BeforeAuditScope = "20260809024707_RenameExternalIdentityProviderToIssuer";
    private const string AddAuditScope = "20260809184931_AddAuditScope";
    private const long AuditDownPauseLockKey = 412_208_309;

    [Fact]
    public async Task Add_audit_scope_backfills_legacy_events_and_removes_the_legacy_appender()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeAuditScope);
        var companyId = Guid.NewGuid();

        await using (var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString))
        {
            await runtime.OpenAsync();
            await InsertLegacyEventAsync(runtime, companyId, "foundation.legacy.company");
            await InsertLegacyEventAsync(runtime, null, "company_context_required");
        }

        await using (var legacyAdministrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var legacyRows = new NpgsqlCommand(
            "SELECT count(*) FILTER (WHERE requested_company_id IS NULL), count(*) FILTER (WHERE requested_company_id IS NOT NULL) FROM audit.audit_events",
            legacyAdministrator))
        await using (var legacyReader = await legacyRows.ExecuteReaderAsync())
        {
            Assert.True(await legacyReader.ReadAsync());
            Assert.Equal(1L, legacyReader.GetInt64(0));
            Assert.Equal(1L, legacyReader.GetInt64(1));
        }

        await migrator.Database.MigrateAsync(AddAuditScope);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT action, scope, requested_company_id
            FROM audit.audit_events
            ORDER BY action
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("company_context_required", reader.GetString(0));
        Assert.Equal("global", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("foundation.legacy.company", reader.GetString(0));
        Assert.Equal("company", reader.GetString(1));
        Assert.False(reader.IsDBNull(2));

        await reader.CloseAsync();
        await using var grants = new NpgsqlCommand(
            "SELECT has_schema_privilege('vinx_audit_owner', 'audit', 'CREATE')",
            administrator);
        Assert.False((bool)(await grants.ExecuteScalarAsync() ?? true));
    }

    [Fact]
    public async Task Downgrade_restores_the_legacy_appender_when_every_event_is_company_scoped()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using (var companyRuntime = new NpgsqlConnection(fixture.RuntimeConnectionString))
        {
            await companyRuntime.OpenAsync();
            await AppendCurrentEventAsync(companyRuntime, "company", Guid.NewGuid());
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeAuditScope);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regprocedure('audit.append_event(uuid,uuid,text,text,text,uuid,text,uuid,text,jsonb)') IS NOT NULL,
                to_regprocedure('audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)') IS NULL,
                NOT EXISTS (
                    SELECT 1 FROM pg_attribute
                    WHERE attrelid = 'audit.audit_events'::regclass AND attname = 'scope'),
                pg_get_userbyid(appender.proowner),
                appender.prosecdef,
                appender.proconfig,
                has_function_privilege(
                    'vinx_runtime',
                    'audit.append_event(uuid,uuid,text,text,text,uuid,text,uuid,text,jsonb)',
                    'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(appender.proacl, acldefault('f', appender.proowner))) AS privilege
                    WHERE privilege.grantee = 0 AND privilege.privilege_type = 'EXECUTE'),
                NOT has_schema_privilege('vinx_audit_owner', 'audit', 'CREATE'),
                pg_get_constraintdef(table_constraint.oid) LIKE
                    '%requested_company_id IS NOT NULL%company_context_required%denied%'
            FROM pg_proc AS appender
            CROSS JOIN pg_constraint AS table_constraint
            WHERE appender.oid = 'audit.append_event(uuid,uuid,text,text,text,uuid,text,uuid,text,jsonb)'::regprocedure
              AND table_constraint.conrelid = 'audit.audit_events'::regclass
              AND table_constraint.conname = 'ck_audit_events_requested_company'
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal("vinx_audit_owner", reader.GetString(3));
        Assert.True(reader.GetBoolean(4));
        Assert.Equal(["search_path=audit, pg_catalog"], reader.GetFieldValue<string[]>(5));
        Assert.True(reader.GetBoolean(6));
        Assert.True(reader.GetBoolean(7));
        Assert.True(reader.GetBoolean(8));
        Assert.True(reader.GetBoolean(9));

        await reader.CloseAsync();
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await AppendLegacyEventAsync(runtime, null, "company_context_required", "denied");
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await AppendLegacyEventAsync(runtime, null, "foundation.test", "success"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Downgrade_rejects_global_events_before_rewriting_or_deleting_them()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var correlationId = Guid.NewGuid();
        await using (var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString))
        {
            await runtime.OpenAsync();
            await AppendCurrentEventAsync(runtime, "global", null, correlationId);
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync(BeforeAuditScope));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT scope, requested_company_id,
                   to_regprocedure('audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)') IS NOT NULL,
                   has_schema_privilege('vinx_audit_owner', 'audit', 'CREATE')
            FROM audit.audit_events
            WHERE correlation_id = @correlation_id
            """, administrator);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("global", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
    }

    [Fact]
    public async Task Downgrade_blocks_a_legacy_compatible_global_append_before_destructive_ddl()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await CreateDowngradePauseTriggerAsync(administrator);
        await using var coordinator = await fixture.OpenAdministratorConnectionAsync();
        await AcquirePauseLockAsync(coordinator);
        await using var migrator = fixture.CreateMigratorDbContext();
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();

        var downgradeTask = migrator.Database.MigrateAsync(BeforeAuditScope);
        try
        {
            using var pauseSignalCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitForPendingAdvisoryLockAsync(administrator, pauseSignalCancellation.Token);

            var appendTask = AppendCurrentEventAsync(
                runtime,
                "global",
                null,
                action: "company_context_required",
                outcome: "denied");
            using var blockSignalCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var blockSignal = WaitForRuntimeAppendToBlockAsync(
                administrator,
                runtime.ProcessID,
                blockSignalCancellation.Token);
            var firstCompleted = await Task.WhenAny(appendTask, blockSignal);
            blockSignalCancellation.Cancel();
            await IgnoreCancellationAsync(blockSignal);

            await ReleasePauseLockAsync(coordinator);
            await downgradeTask;

            Assert.Same(blockSignal, firstCompleted);
            await Assert.ThrowsAsync<PostgresException>(async () => await appendTask);
        }
        finally
        {
            await ReleasePauseLockAsync(coordinator);
            await AwaitDowngradeAsync(downgradeTask);
            await DropDowngradePauseTriggerAsync(administrator);
        }
    }

    private static async Task InsertLegacyEventAsync(NpgsqlConnection runtime, Guid? companyId, string action)
        => await AppendLegacyEventAsync(runtime, companyId, action, "denied");

    private static async Task AppendLegacyEventAsync(
        NpgsqlConnection runtime,
        Guid? companyId,
        string action,
        string outcome)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT audit.append_event(
                @actor_user_id, @requested_company_id, 'web', @action, 'test', NULL,
                @outcome, @correlation_id, 'redacted', '{}'::jsonb)
            """, runtime);
        command.Parameters.AddWithValue("actor_user_id", Guid.NewGuid());
        command.Parameters.AddWithValue("requested_company_id", (object?)companyId ?? DBNull.Value);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AppendCurrentEventAsync(
        NpgsqlConnection runtime,
        string scope,
        Guid? companyId,
        Guid? correlationId = null,
        string action = "foundation.test",
        string outcome = "success")
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT audit.append_event(
                @actor_user_id, @scope, @requested_company_id, 'web', @action, 'test', NULL,
                @outcome, @correlation_id, 'redacted', '{}'::jsonb)
            """, runtime);
        command.Parameters.AddWithValue("actor_user_id", Guid.NewGuid());
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("requested_company_id", (object?)companyId ?? DBNull.Value);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("correlation_id", correlationId ?? Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateDowngradePauseTriggerAsync(NpgsqlConnection administrator)
    {
        await using var command = new NpgsqlCommand(
            $"""
            CREATE FUNCTION public.pause_audit_scope_downgrade()
            RETURNS event_trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                PERFORM pg_advisory_xact_lock({AuditDownPauseLockKey});
            END;
            $function$;

            CREATE EVENT TRIGGER pause_audit_scope_downgrade
            ON ddl_command_start
            WHEN TAG IN ('DROP FUNCTION')
            EXECUTE FUNCTION public.pause_audit_scope_downgrade();
            """, administrator);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDowngradePauseTriggerAsync(NpgsqlConnection administrator)
    {
        await using var command = new NpgsqlCommand(
            """
            DROP EVENT TRIGGER IF EXISTS pause_audit_scope_downgrade;
            DROP FUNCTION IF EXISTS public.pause_audit_scope_downgrade();
            """, administrator);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AcquirePauseLockAsync(NpgsqlConnection coordinator)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_lock(@lock_key);", coordinator);
        command.Parameters.AddWithValue("lock_key", AuditDownPauseLockKey);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ReleasePauseLockAsync(NpgsqlConnection coordinator)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_unlock(@lock_key);", coordinator);
        command.Parameters.AddWithValue("lock_key", AuditDownPauseLockKey);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForPendingAdvisoryLockAsync(
        NpgsqlConnection administrator,
        CancellationToken cancellationToken)
    {
        await WaitUntilAsync(async () =>
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks AS locks
                    INNER JOIN pg_stat_activity AS activity ON activity.pid = locks.pid
                    WHERE activity.usename = 'vinx_migrator'
                      AND locks.locktype = 'advisory'
                      AND NOT locks.granted)
                """, administrator);
            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }, cancellationToken);
    }

    private static async Task WaitForRuntimeAppendToBlockAsync(
        NpgsqlConnection administrator,
        int processId,
        CancellationToken cancellationToken)
    {
        await WaitUntilAsync(async () =>
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE pid = @process_id
                      AND wait_event_type = 'Lock')
                """, administrator);
            command.Parameters.AddWithValue("process_id", processId);
            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }, cancellationToken);
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task AwaitDowngradeAsync(Task downgradeTask)
    {
        try
        {
            await downgradeTask;
        }
        catch (PostgresException)
        {
        }
    }
}
