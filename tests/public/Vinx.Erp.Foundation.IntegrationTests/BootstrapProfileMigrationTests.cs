using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class BootstrapProfileMigrationTests
{
    private const string BeforeBootstrapProfile = "20260814141932_AddAiWritesConfirmCapability";

    [Fact]
    public async Task Migration_exposes_the_current_profile_through_a_narrow_security_definer_function()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regprocedure('foundation.current_actor_profile()') IS NOT NULL,
                function.prosecdef,
                function.proconfig,
                pg_get_userbyid(function.proowner),
                has_function_privilege(
                    'vinx_runtime',
                    'foundation.current_actor_profile()',
                    'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(function.proacl, acldefault('f', function.proowner))) AS privilege
                    WHERE privilege.grantee = 0 AND privilege.privilege_type = 'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(function.proacl, acldefault('f', function.proowner))) AS privilege
                    WHERE privilege.privilege_type = 'EXECUTE'
                      AND privilege.grantee NOT IN (
                          function.proowner,
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_runtime'))),
                authorization_owner.rolcanlogin,
                authorization_owner.rolinherit,
                authorization_owner.rolbypassrls,
                has_schema_privilege('vinx_authorization_owner', 'foundation', 'USAGE'),
                has_schema_privilege('vinx_authorization_owner', 'foundation', 'CREATE'),
                has_table_privilege('vinx_authorization_owner', 'foundation.users', 'SELECT'),
                has_column_privilege('vinx_authorization_owner', 'foundation.users', 'id', 'SELECT'),
                has_column_privilege('vinx_authorization_owner', 'foundation.users', 'display_name', 'SELECT'),
                has_column_privilege('vinx_authorization_owner', 'foundation.users', 'email', 'SELECT'),
                has_column_privilege('vinx_runtime', 'foundation.users', 'id', 'SELECT'),
                has_column_privilege('vinx_runtime', 'foundation.users', 'display_name', 'SELECT'),
                has_column_privilege('vinx_runtime', 'foundation.users', 'email', 'SELECT'),
                NOT EXISTS (
                    SELECT 1
                    FROM pg_policy
                    WHERE polrelid = 'foundation.users'::regclass)
            FROM pg_proc AS function
            CROSS JOIN pg_roles AS authorization_owner
            WHERE function.oid = 'foundation.current_actor_profile()'::regprocedure
              AND authorization_owner.rolname = 'vinx_authorization_owner'
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(["search_path=foundation, pg_catalog"], reader.GetFieldValue<string[]>(2));
        Assert.Equal("vinx_authorization_owner", reader.GetString(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
        Assert.False(reader.GetBoolean(8));
        Assert.False(reader.GetBoolean(9));
        Assert.True(reader.GetBoolean(10));
        Assert.False(reader.GetBoolean(11));
        Assert.False(reader.GetBoolean(12));
        Assert.True(reader.GetBoolean(13));
        Assert.True(reader.GetBoolean(14));
        Assert.False(reader.GetBoolean(15));
        Assert.False(reader.GetBoolean(16));
        Assert.False(reader.GetBoolean(17));
        Assert.False(reader.GetBoolean(18));
        Assert.True(reader.GetBoolean(19));
    }

    [Fact]
    public async Task Runtime_function_reads_only_the_transaction_local_actor_profile()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var actorId = Guid.NewGuid();
        var otherActorId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var seed = new NpgsqlCommand(
            """
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES
                (@actor_id, 'Ana', 'ana-bootstrap-profile@example.test', clock_timestamp()),
                (@other_actor_id, 'Bea', 'bea-bootstrap-profile@example.test', clock_timestamp());
            """, administrator))
        {
            seed.Parameters.AddWithValue("actor_id", actorId);
            seed.Parameters.AddWithValue("other_actor_id", otherActorId);
            await seed.ExecuteNonQueryAsync();
        }

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using (var directRead = new NpgsqlCommand(
            "SELECT id, display_name, email FROM foundation.users", runtime))
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
                await directRead.ExecuteReaderAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }

        await using (var transaction = await runtime.BeginTransactionAsync())
        {
            await using var setActor = new NpgsqlCommand(
                "SELECT set_config('app.user_id', @actor_id, true)", runtime, transaction);
            setActor.Parameters.AddWithValue("actor_id", actorId.ToString());
            await setActor.ExecuteNonQueryAsync();

            await using var profile = new NpgsqlCommand(
                "SELECT user_id, display_name FROM foundation.current_actor_profile()",
                runtime,
                transaction);
            await using var reader = await profile.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal(actorId, reader.GetGuid(0));
            Assert.Equal("Ana", reader.GetString(1));
            Assert.False(await reader.ReadAsync());
        }

        await using var noActor = new NpgsqlCommand(
            "SELECT count(*) FROM foundation.current_actor_profile()", runtime);
        Assert.Equal(0L, await noActor.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Empty_state_downgrade_removes_profile_function_and_column_grants()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeBootstrapProfile);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regprocedure('foundation.current_actor_profile()') IS NULL,
                NOT has_column_privilege('vinx_authorization_owner', 'foundation.users', 'id', 'SELECT'),
                NOT has_column_privilege('vinx_authorization_owner', 'foundation.users', 'display_name', 'SELECT'),
                NOT has_column_privilege('vinx_authorization_owner', 'foundation.users', 'email', 'SELECT')
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
    }
}
