using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class SessionMigrationTests
{
    private const string BeforeInternalSessionState = "20260809184931_AddAuditScope";
    private const string BeforeSelfSessionRevocation = "20260810002901_AddInternalSessionState";
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
                has_schema_privilege('vinx_identity_owner', 'foundation', 'USAGE'),
                has_schema_privilege('vinx_identity_owner', 'foundation', 'CREATE'),
                has_table_privilege('vinx_identity_owner', 'foundation.users', 'SELECT'),
                has_column_privilege('vinx_identity_owner', 'foundation.users', 'sessions_valid_after', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.users', 'sessions_valid_after', 'UPDATE'),
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
        Assert.Equal("vinx_identity_owner", reader.GetString(11));
        Assert.True(reader.GetBoolean(12));
        Assert.False(reader.GetBoolean(13));
        Assert.True(reader.GetBoolean(14));
        Assert.True(reader.GetBoolean(15));
        Assert.False(reader.GetBoolean(16));
        Assert.True(reader.GetBoolean(17));
        Assert.True(reader.GetBoolean(18));
    }

    [Fact]
    public async Task Migration_exposes_self_revocation_only_through_a_secure_definer_function()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regprocedure('foundation.revoke_all_user_sessions(uuid)') IS NOT NULL,
                has_function_privilege(
                    'vinx_runtime',
                    'foundation.revoke_all_user_sessions(uuid)',
                    'EXECUTE'),
                has_table_privilege('vinx_runtime', 'foundation.users', 'UPDATE'),
                has_table_privilege('vinx_runtime', 'foundation.user_sessions', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.users', 'sessions_valid_after', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.user_sessions', 'revoked_at', 'UPDATE'),
                has_column_privilege('vinx_identity_owner', 'foundation.users', 'sessions_valid_after', 'UPDATE'),
                has_column_privilege('vinx_identity_owner', 'foundation.user_sessions', 'revoked_at', 'UPDATE'),
                has_column_privilege('vinx_identity_owner', 'foundation.user_sessions', 'revoked_by_user_id', 'UPDATE'),
                has_column_privilege('vinx_identity_owner', 'foundation.user_sessions', 'revocation_reason', 'UPDATE'),
                function.prosecdef,
                function.proconfig,
                pg_get_userbyid(function.proowner),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(function.proacl, acldefault('f', function.proowner))) AS privilege
                    WHERE privilege.grantee = 0 AND privilege.privilege_type = 'EXECUTE')
            FROM pg_proc AS function
            WHERE function.oid = 'foundation.revoke_all_user_sessions(uuid)'::regprocedure
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
        Assert.True(reader.GetBoolean(7));
        Assert.True(reader.GetBoolean(8));
        Assert.True(reader.GetBoolean(9));
        Assert.True(reader.GetBoolean(10));
        Assert.Equal(["search_path=foundation, pg_catalog"], reader.GetFieldValue<string[]>(11));
        Assert.Equal("vinx_identity_owner", reader.GetString(12));
        Assert.True(reader.GetBoolean(13));
    }

    [Fact]
    public async Task Runtime_function_revokes_active_sessions_with_one_database_timestamp_and_keeps_no_change_monotonic()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var userId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var setup = new NpgsqlCommand(
            """
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES (@user_id, 'Função', 'function-revoke@example.test', clock_timestamp());
            INSERT INTO foundation.user_sessions (
                id, user_id, issuer, provider_session_id, provider_authenticated_at, token_expires_at)
            VALUES
                (gen_random_uuid(), @user_id, 'https://project.supabase.co/auth/v1', gen_random_uuid(),
                 '2026-08-09T12:00:00+00:00', '2026-08-09T13:00:00+00:00'),
                (gen_random_uuid(), @user_id, 'https://project.supabase.co/auth/v1', gen_random_uuid(),
                 '2026-08-09T12:01:00+00:00', '2026-08-09T13:01:00+00:00');
            """, administrator))
        {
            setup.Parameters.AddWithValue("user_id", userId);
            await setup.ExecuteNonQueryAsync();
        }

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var revoke = new NpgsqlCommand(
            "SELECT foundation.revoke_all_user_sessions(@user_id)", runtime);
        revoke.Parameters.AddWithValue("user_id", userId);
        Assert.Equal(2, Convert.ToInt32(await revoke.ExecuteScalarAsync()));

        await using var verify = await fixture.OpenAdministratorConnectionAsync();
        await using var state = new NpgsqlCommand(
            """
            SELECT
                (SELECT sessions_valid_after FROM foundation.users WHERE id = @user_id),
                count(*) FILTER (WHERE revoked_at IS NOT NULL),
                count(DISTINCT revoked_at),
                bool_and(revoked_by_user_id = @user_id AND revocation_reason = 'self_revoke_all')
            FROM foundation.user_sessions
            WHERE user_id = @user_id
            """, verify);
        state.Parameters.AddWithValue("user_id", userId);
        await using var reader = await state.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var barrier = reader.GetDateTime(0);
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.True(reader.GetBoolean(3));
        await reader.CloseAsync();

        await using var noChange = new NpgsqlCommand(
            "SELECT foundation.revoke_all_user_sessions(@user_id)", runtime);
        noChange.Parameters.AddWithValue("user_id", userId);
        Assert.Equal(0, Convert.ToInt32(await noChange.ExecuteScalarAsync()));
        await using var afterNoChange = new NpgsqlCommand(
            "SELECT sessions_valid_after FROM foundation.users WHERE id = @user_id", verify);
        afterNoChange.Parameters.AddWithValue("user_id", userId);
        Assert.Equal(barrier, await afterNoChange.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("barrier")]
    [InlineData("revocation")]
    [InlineData("audit")]
    public async Task Self_revocation_downgrade_fails_closed_when_observable_state_exists(string state)
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var userId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var setup = new NpgsqlCommand(
            """
            INSERT INTO foundation.users (id, display_name, email, created_at, sessions_valid_after)
            VALUES (@user_id, 'Downgrade', 'downgrade-revoke@example.test', clock_timestamp(),
                CASE WHEN @state = 'barrier' THEN clock_timestamp() ELSE NULL END);
            INSERT INTO foundation.user_sessions (
                id, user_id, issuer, provider_session_id, provider_authenticated_at, token_expires_at,
                revoked_at, revoked_by_user_id, revocation_reason)
            SELECT gen_random_uuid(), @user_id, 'https://project.supabase.co/auth/v1', gen_random_uuid(),
                '2026-08-09T12:00:00+00:00', '2026-08-09T13:00:00+00:00',
                CASE WHEN @state = 'revocation' THEN clock_timestamp() ELSE NULL END,
                CASE WHEN @state = 'revocation' THEN @user_id ELSE NULL END,
                CASE WHEN @state = 'revocation' THEN 'self_revoke_all' ELSE NULL END;
            INSERT INTO audit.audit_events (
                id, actor_user_id, scope, requested_company_id, source, action, resource_type, resource_id,
                outcome, correlation_id, reason, metadata_json, occurred_at)
            SELECT gen_random_uuid(), @user_id, 'global', NULL, 'web', 'identity_sessions_revoked', 'user',
                @user_id, 'success', gen_random_uuid(), NULL, '{"revoked_session_count":1}'::jsonb, clock_timestamp()
            WHERE @state = 'audit';
            """, administrator))
        {
            setup.Parameters.AddWithValue("user_id", userId);
            setup.Parameters.AddWithValue("state", state);
            await setup.ExecuteNonQueryAsync();
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync(BeforeSelfSessionRevocation));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Successful_self_revocation_downgrade_removes_function_and_owner_session_grants()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeSelfSessionRevocation);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regprocedure('foundation.revoke_all_user_sessions(uuid)') IS NULL,
                NOT has_column_privilege(
                    'vinx_identity_owner', 'foundation.user_sessions', 'user_id', 'SELECT'),
                NOT has_column_privilege(
                    'vinx_identity_owner', 'foundation.user_sessions', 'revoked_at', 'SELECT'),
                NOT has_column_privilege(
                    'vinx_identity_owner', 'foundation.user_sessions', 'revoked_at', 'UPDATE'),
                NOT has_column_privilege(
                    'vinx_identity_owner', 'foundation.user_sessions', 'revoked_by_user_id', 'UPDATE'),
                NOT has_column_privilege(
                    'vinx_identity_owner', 'foundation.user_sessions', 'revocation_reason', 'UPDATE')
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
    }

    [Fact]
    public async Task Runtime_can_lock_an_existing_user_and_read_its_session_barrier_through_the_function()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var userId = Guid.NewGuid();
        var sessionsValidAfter = DateTimeOffset.Parse("2026-08-10T12:00:00+00:00");

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var setup = new NpgsqlCommand(
            """
            INSERT INTO foundation.users (id, display_name, email, created_at, sessions_valid_after)
            VALUES (@id, 'Lock runtime', 'runtime-lock@example.test', clock_timestamp(), @sessions_valid_after)
            """, administrator))
        {
            setup.Parameters.AddWithValue("id", userId);
            setup.Parameters.AddWithValue("sessions_valid_after", sessionsValidAfter);
            await setup.ExecuteNonQueryAsync();
        }

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var runtimeTransaction = await runtime.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT sessions_valid_after FROM foundation.lock_user_for_session_admission(@user_id)",
            runtime,
            runtimeTransaction);
        command.Parameters.AddWithValue("user_id", userId);

        var actual = await command.ExecuteScalarAsync();

        Assert.Equal(sessionsValidAfter.UtcDateTime, Assert.IsType<DateTime>(actual));

        await using var updater = await fixture.OpenAdministratorConnectionAsync();
        await using var observer = await fixture.OpenAdministratorConnectionAsync();
        var blockedAdvance = AdvanceSessionsValidAfterAsync(updater, userId);
        var lockReleased = false;
        try
        {
            using var lockWaitCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitForTransactionIdLockAsync(
                observer,
                updater.ProcessID,
                lockWaitCancellation.Token);

            await runtimeTransaction.RollbackAsync();
            lockReleased = true;
            await blockedAdvance;
        }
        finally
        {
            if (!lockReleased)
            {
                await runtimeTransaction.RollbackAsync();
            }

            await AwaitOperationAsync(blockedAdvance);
        }
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

        await using var missingReason = new NpgsqlCommand(
            """
            INSERT INTO foundation.user_sessions (
                id, user_id, issuer, provider_session_id, provider_authenticated_at, token_expires_at,
                revoked_at, revoked_by_user_id, revocation_reason)
            VALUES (
                @id, @user_id, 'https://project.supabase.co/auth/v1', @provider_session_id,
                '2026-08-09T12:00:00+00:00', '2026-08-09T13:00:00+00:00',
                '2026-08-09T12:30:00+00:00', @revoked_by_user_id, NULL)
            """, runtime);
        missingReason.Parameters.AddWithValue("id", Guid.NewGuid());
        missingReason.Parameters.AddWithValue("user_id", userId);
        missingReason.Parameters.AddWithValue("provider_session_id", Guid.NewGuid());
        missingReason.Parameters.AddWithValue("revoked_by_user_id", userId);
        var missingReasonException = await Assert.ThrowsAsync<PostgresException>(async () =>
            await missingReason.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, missingReasonException.SqlState);
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

        await using (var selfRevocationMigrator = fixture.CreateMigratorDbContext())
        {
            await selfRevocationMigrator.Database.MigrateAsync(BeforeSelfSessionRevocation);
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
            await WaitForDowngradeLocksAsync(administrator, lockSignalCancellation.Token);

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

    [Fact]
    public async Task Downgrade_blocks_a_concurrent_session_barrier_advance_while_its_exclusive_locks_are_held()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var userId = Guid.NewGuid();

        await using (var administratorContext = fixture.CreateAdministratorDbContext())
        {
            administratorContext.Users.Add(new User(userId, "Barreira concorrente", "barrier-concurrency@example.test"));
            await administratorContext.SaveChangesAsync();
        }

        await using (var selfRevocationMigrator = fixture.CreateMigratorDbContext())
        {
            await selfRevocationMigrator.Database.MigrateAsync(BeforeSelfSessionRevocation);
        }

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await CreateDowngradePauseTriggerAsync(administrator);
        await using var coordinator = await fixture.OpenAdministratorConnectionAsync();
        await AcquirePauseLockAsync(coordinator);
        await using var migrator = fixture.CreateMigratorDbContext();

        var downgradeTask = migrator.Database.MigrateAsync(BeforeInternalSessionState);
        try
        {
            using var lockSignalCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitForDowngradeLocksAsync(administrator, lockSignalCancellation.Token);

            var advanceBarrierTask = AdvanceSessionsValidAfterAsync(administrator, userId);
            var firstCompleted = await Task.WhenAny(advanceBarrierTask, Task.Delay(TimeSpan.FromMilliseconds(500)));

            Assert.NotSame(advanceBarrierTask, firstCompleted);

            await ReleasePauseLockAsync(coordinator);
            await downgradeTask;
            var exception = await Assert.ThrowsAsync<PostgresException>(async () => await advanceBarrierTask);
            Assert.Equal(PostgresErrorCodes.UndefinedColumn, exception.SqlState);
        }
        finally
        {
            await ReleasePauseLockAsync(coordinator);
            await AwaitMigrationAsync(downgradeTask);
            await DropDowngradePauseTriggerAsync(administrator);
        }
    }

    [Fact]
    public async Task Successful_downgrade_removes_the_identity_owner_session_grants()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeInternalSessionState);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regprocedure('foundation.lock_user_for_session_admission(uuid)') IS NULL,
                NOT has_schema_privilege('vinx_identity_owner', 'foundation', 'USAGE'),
                NOT has_table_privilege('vinx_identity_owner', 'foundation.users', 'SELECT'),
                NOT has_table_privilege('vinx_identity_owner', 'foundation.users', 'UPDATE'),
                NOT has_any_column_privilege('vinx_identity_owner', 'foundation.users', 'UPDATE'),
                NOT EXISTS (
                    SELECT 1
                    FROM pg_attribute AS attribute
                    CROSS JOIN LATERAL aclexplode(attribute.attacl) AS privilege
                    WHERE attribute.attrelid = 'foundation.users'::regclass
                      AND NOT attribute.attisdropped
                      AND privilege.grantee = 'vinx_identity_owner'::regrole
                      AND privilege.privilege_type = 'UPDATE')
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
    }

    [Fact]
    public async Task Migration_exposes_the_required_session_indexes_and_restrictive_foreign_keys()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM pg_index AS index
                    WHERE index.indexrelid = 'foundation.ix_user_sessions_issuer_provider_session_id'::regclass
                      AND index.indisunique
                      AND ARRAY(
                          SELECT attribute.attname::text
                          FROM unnest(index.indkey) WITH ORDINALITY AS key_column(attnum, ordinal_position)
                          INNER JOIN pg_attribute AS attribute
                              ON attribute.attrelid = index.indrelid
                              AND attribute.attnum = key_column.attnum
                          ORDER BY key_column.ordinal_position) = ARRAY['issuer', 'provider_session_id']),
                EXISTS (
                    SELECT 1
                    FROM pg_index AS index
                    WHERE index.indexrelid = 'foundation.ix_user_sessions_user_id_revoked_at'::regclass
                      AND NOT index.indisunique
                      AND ARRAY(
                          SELECT attribute.attname::text
                          FROM unnest(index.indkey) WITH ORDINALITY AS key_column(attnum, ordinal_position)
                          INNER JOIN pg_attribute AS attribute
                              ON attribute.attrelid = index.indrelid
                              AND attribute.attnum = key_column.attnum
                          ORDER BY key_column.ordinal_position) = ARRAY['user_id', 'revoked_at']),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint AS foreign_key
                    WHERE foreign_key.conrelid = 'foundation.user_sessions'::regclass
                      AND foreign_key.conname = 'fk_user_sessions_users_user_id'
                      AND foreign_key.confdeltype = 'r'
                      AND foreign_key.confrelid = 'foundation.users'::regclass
                      AND ARRAY(
                          SELECT attribute.attname::text
                          FROM unnest(foreign_key.conkey) WITH ORDINALITY AS key_column(attnum, ordinal_position)
                          INNER JOIN pg_attribute AS attribute
                              ON attribute.attrelid = foreign_key.conrelid
                              AND attribute.attnum = key_column.attnum
                          ORDER BY key_column.ordinal_position) = ARRAY['user_id']
                      AND ARRAY(
                          SELECT attribute.attname::text
                          FROM unnest(foreign_key.confkey) WITH ORDINALITY AS key_column(attnum, ordinal_position)
                          INNER JOIN pg_attribute AS attribute
                              ON attribute.attrelid = foreign_key.confrelid
                              AND attribute.attnum = key_column.attnum
                          ORDER BY key_column.ordinal_position) = ARRAY['id']),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint AS foreign_key
                    WHERE foreign_key.conrelid = 'foundation.user_sessions'::regclass
                      AND foreign_key.conname = 'fk_user_sessions_users_revoked_by_user_id'
                      AND foreign_key.confdeltype = 'r'
                      AND foreign_key.confrelid = 'foundation.users'::regclass
                      AND ARRAY(
                          SELECT attribute.attname::text
                          FROM unnest(foreign_key.conkey) WITH ORDINALITY AS key_column(attnum, ordinal_position)
                          INNER JOIN pg_attribute AS attribute
                              ON attribute.attrelid = foreign_key.conrelid
                              AND attribute.attnum = key_column.attnum
                          ORDER BY key_column.ordinal_position) = ARRAY['revoked_by_user_id']
                      AND ARRAY(
                          SELECT attribute.attname::text
                          FROM unnest(foreign_key.confkey) WITH ORDINALITY AS key_column(attnum, ordinal_position)
                          INNER JOIN pg_attribute AS attribute
                              ON attribute.attrelid = foreign_key.confrelid
                              AND attribute.attnum = key_column.attnum
                          ORDER BY key_column.ordinal_position) = ARRAY['id'])
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
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

    private static async Task AdvanceSessionsValidAfterAsync(NpgsqlConnection administrator, Guid userId)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE foundation.users
            SET sessions_valid_after = clock_timestamp()
            WHERE id = @user_id
            """, administrator);
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForTransactionIdLockAsync(
        NpgsqlConnection observer,
        int blockedBackendProcessId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks
                WHERE pid = @backend_pid
                      AND locktype = 'transactionid'
                      AND mode = 'ShareLock'
                      AND NOT granted)
                """, observer);
            command.Parameters.AddWithValue("backend_pid", blockedBackendProcessId);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        throw new Xunit.Sdk.XunitException("The barrier advance did not block on the runtime transaction.");
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

    private static async Task WaitForDowngradeLocksAsync(
        NpgsqlConnection administrator,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*) = 2
                FROM (
                    SELECT DISTINCT relation.relname
                    FROM pg_locks AS lock
                    INNER JOIN pg_class AS relation ON relation.oid = lock.relation
                    INNER JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
                    WHERE schema.nspname = 'foundation'
                      AND relation.relname IN ('user_sessions', 'users')
                      AND lock.mode = 'AccessExclusiveLock'
                      AND lock.granted) AS locked_relations
                """, administrator);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new Xunit.Sdk.XunitException("The downgrade did not acquire both session-state table locks.");
    }

    private static async Task AwaitMigrationAsync(Task migrationTask)
    {
        try
        {
            await migrationTask;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
        }
    }

    private static async Task AwaitOperationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (PostgresException)
        {
        }
    }
}
