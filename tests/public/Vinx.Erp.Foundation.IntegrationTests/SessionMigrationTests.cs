using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class SessionMigrationTests
{
    private const string BeforeInternalSessionState = "20260809184931_AddAuditScope";
    private const long DowngradePauseLockKey = 719_364_225;

    [Fact]
    public async Task Migration_creates_locked_session_state_with_the_runtime_minimum_grants()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regclass('foundation.user_sessions') IS NOT NULL,
                to_regprocedure('foundation.lock_user_for_session_admission(uuid)') IS NOT NULL,
                has_table_privilege('vinx_runtime', 'foundation.user_sessions', 'SELECT'),
                has_table_privilege('vinx_runtime', 'foundation.user_sessions', 'INSERT'),
                has_table_privilege('vinx_runtime', 'foundation.user_sessions', 'UPDATE'),
                has_table_privilege('vinx_runtime', 'foundation.user_sessions', 'DELETE'),
                has_table_privilege('vinx_runtime', 'foundation.users', 'SELECT'),
                has_function_privilege(
                    'vinx_runtime',
                    'foundation.lock_user_for_session_admission(uuid)',
                    'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(lock_function.proacl, acldefault('f', lock_function.proowner))) AS privilege
                    WHERE privilege.grantee = 0 AND privilege.privilege_type = 'EXECUTE'),
                lock_function.prosecdef,
                lock_function.proconfig,
                pg_get_userbyid(lock_function.proowner),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.user_sessions'::regclass
                      AND conname = 'ck_user_sessions_authenticated_before_expiration'),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.user_sessions'::regclass
                      AND conname = 'ck_user_sessions_revocation_complete')
            FROM pg_proc AS lock_function
            WHERE lock_function.oid = 'foundation.lock_user_for_session_admission(uuid)'::regprocedure
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
        Assert.True(reader.GetBoolean(7));
        Assert.True(reader.GetBoolean(8));
        Assert.True(reader.GetBoolean(9));
        Assert.Equal(["search_path=foundation, pg_catalog"], reader.GetFieldValue<string[]>(10));
        Assert.Equal("vinx_migrator", reader.GetString(11));
        Assert.True(reader.GetBoolean(12));
        Assert.True(reader.GetBoolean(13));
    }

    [Fact]
    public async Task Downgrade_fails_closed_without_deleting_existing_session_state()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        await using (var administrator = fixture.CreateAdministratorDbContext())
        {
            administrator.Users.Add(new User(userId, "Sessão", "session@example.test"));
            administrator.UserSessions.Add(new UserSession(
                sessionId,
                userId,
                "https://project.supabase.co/auth/v1",
                Guid.NewGuid(),
                DateTimeOffset.Parse("2026-08-09T12:00:00+00:00"),
                DateTimeOffset.Parse("2026-08-09T13:00:00+00:00")));
            await administrator.SaveChangesAsync();
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync(BeforeInternalSessionState));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);

        await using var verify = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM foundation.user_sessions WHERE id = @session_id", verify);
        command.Parameters.AddWithValue("session_id", sessionId);
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Downgrade_fails_closed_without_removing_a_session_barrier()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var userId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var setup = new NpgsqlCommand(
            """
            INSERT INTO foundation.users (id, display_name, email, created_at, sessions_valid_after)
            VALUES (@id, 'Barreira', 'barrier@example.test', clock_timestamp(), clock_timestamp())
            """, administrator))
        {
            setup.Parameters.AddWithValue("id", userId);
            await setup.ExecuteNonQueryAsync();
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync(BeforeInternalSessionState));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);

        await using var verify = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT sessions_valid_after IS NOT NULL FROM foundation.users WHERE id = @user_id", verify);
        command.Parameters.AddWithValue("user_id", userId);
        Assert.True((bool)(await command.ExecuteScalarAsync() ?? false));
    }

    [Fact]
    public async Task Session_table_rejects_invalid_temporal_and_partial_revocation_state()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var userId = Guid.NewGuid();

        await using (var administratorContext = fixture.CreateAdministratorDbContext())
        {
            administratorContext.Users.Add(new User(userId, "Invariantes", "invariants@example.test"));
            await administratorContext.SaveChangesAsync();
        }

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();

        var temporalException = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertSessionAsync(runtime, userId, "2026-08-09T12:00:00+00:00", "2026-08-09T12:00:00+00:00"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, temporalException.SqlState);

        await using var partialRevocation = new NpgsqlCommand(
            """
            INSERT INTO foundation.user_sessions (
                id, user_id, issuer, provider_session_id, provider_authenticated_at, token_expires_at, revoked_at)
            VALUES (
                @id, @user_id, 'https://project.supabase.co/auth/v1', @provider_session_id,
                '2026-08-09T12:00:00+00:00', '2026-08-09T13:00:00+00:00', '2026-08-09T12:30:00+00:00')
            """, runtime);
        partialRevocation.Parameters.AddWithValue("id", Guid.NewGuid());
        partialRevocation.Parameters.AddWithValue("user_id", userId);
        partialRevocation.Parameters.AddWithValue("provider_session_id", Guid.NewGuid());
        var revocationException = await Assert.ThrowsAsync<PostgresException>(async () =>
            await partialRevocation.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, revocationException.SqlState);
    }

    [Fact]
    public async Task Downgrade_blocks_a_concurrent_session_insert_while_its_exclusive_locks_are_held()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var userId = Guid.NewGuid();

        await using (var administratorContext = fixture.CreateAdministratorDbContext())
        {
            administratorContext.Users.Add(new User(userId, "Concorrência", "concurrency@example.test"));
            await administratorContext.SaveChangesAsync();
        }

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await CreateDowngradePauseTriggerAsync(administrator);
        await using var coordinator = await fixture.OpenAdministratorConnectionAsync();
        await AcquirePauseLockAsync(coordinator);
        await using var migrator = fixture.CreateMigratorDbContext();
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();

        var downgradeTask = migrator.Database.MigrateAsync(BeforeInternalSessionState);
        try
        {
            using var lockSignalCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitForSessionTableLockAsync(administrator, lockSignalCancellation.Token);

            var insertTask = InsertSessionAsync(runtime, userId);
            var firstCompleted = await Task.WhenAny(insertTask, Task.Delay(TimeSpan.FromMilliseconds(500)));

            Assert.NotSame(insertTask, firstCompleted);

            await ReleasePauseLockAsync(coordinator);
            await downgradeTask;
            await Assert.ThrowsAsync<PostgresException>(async () => await insertTask);
        }
        finally
        {
            await ReleasePauseLockAsync(coordinator);
            await AwaitMigrationAsync(downgradeTask);
            await DropDowngradePauseTriggerAsync(administrator);
        }
    }

    private static async Task InsertSessionAsync(
        NpgsqlConnection runtime,
        Guid userId,
        string providerAuthenticatedAt = "2026-08-09T12:00:00+00:00",
        string tokenExpiresAt = "2026-08-09T13:00:00+00:00")
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.user_sessions (
                id, user_id, issuer, provider_session_id, provider_authenticated_at, token_expires_at)
            VALUES (
                @id, @user_id, 'https://project.supabase.co/auth/v1', @provider_session_id,
                @provider_authenticated_at, @token_expires_at)
            """, runtime);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("provider_session_id", Guid.NewGuid());
        command.Parameters.AddWithValue("provider_authenticated_at", DateTimeOffset.Parse(providerAuthenticatedAt));
        command.Parameters.AddWithValue("token_expires_at", DateTimeOffset.Parse(tokenExpiresAt));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateDowngradePauseTriggerAsync(NpgsqlConnection administrator)
    {
        await using var command = new NpgsqlCommand(
            $"""
            CREATE FUNCTION public.pause_session_state_downgrade()
            RETURNS event_trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                PERFORM pg_advisory_xact_lock({DowngradePauseLockKey});
            END;
            $function$;

            CREATE EVENT TRIGGER pause_session_state_downgrade
            ON ddl_command_start
            WHEN TAG IN ('DROP FUNCTION')
            EXECUTE FUNCTION public.pause_session_state_downgrade();
            """, administrator);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDowngradePauseTriggerAsync(NpgsqlConnection administrator)
    {
        await using var command = new NpgsqlCommand(
            """
            DROP EVENT TRIGGER IF EXISTS pause_session_state_downgrade;
            DROP FUNCTION IF EXISTS public.pause_session_state_downgrade();
            """, administrator);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AcquirePauseLockAsync(NpgsqlConnection coordinator)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_lock(@lock_key);", coordinator);
        command.Parameters.AddWithValue("lock_key", DowngradePauseLockKey);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ReleasePauseLockAsync(NpgsqlConnection coordinator)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_unlock(@lock_key);", coordinator);
        command.Parameters.AddWithValue("lock_key", DowngradePauseLockKey);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForSessionTableLockAsync(
        NpgsqlConnection administrator,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks AS lock
                    INNER JOIN pg_class AS relation ON relation.oid = lock.relation
                    INNER JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
                    WHERE schema.nspname = 'foundation'
                      AND relation.relname = 'user_sessions'
                      AND lock.mode = 'AccessExclusiveLock'
                      AND lock.granted)
                """, administrator);
            if ((bool)(await command.ExecuteScalarAsync() ?? false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new Xunit.Sdk.XunitException("The downgrade did not acquire the session table lock.");
    }

    private static async Task AwaitMigrationAsync(Task migrationTask)
    {
        try
        {
            await migrationTask;
        }
        catch (PostgresException)
        {
        }
    }
}
