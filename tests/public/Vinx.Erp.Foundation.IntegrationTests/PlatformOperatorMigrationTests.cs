using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PlatformOperatorMigrationTests
{
    [Fact]
    public async Task Migration_creates_a_forced_rls_singleton_and_minimal_function_privileges()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                column_name,
                data_type,
                is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'foundation'
              AND table_name = 'platform_operators'
              AND column_name IN ('singleton', 'external_identity_id')
            ORDER BY ordinal_position;

            SELECT
                relation.relforcerowsecurity,
                EXISTS (
                    SELECT 1
                    FROM pg_constraint AS constraint_row
                    WHERE constraint_row.conrelid = relation.oid
                      AND constraint_row.contype = 'p'
                      AND pg_get_constraintdef(constraint_row.oid) = 'PRIMARY KEY (singleton)'),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint AS constraint_row
                    WHERE constraint_row.conrelid = relation.oid
                      AND constraint_row.contype = 'c'
                      AND pg_get_constraintdef(constraint_row.oid) LIKE '%singleton = 1%'),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint AS constraint_row
                    WHERE constraint_row.conrelid = relation.oid
                      AND constraint_row.contype = 'u'
                      AND pg_get_constraintdef(constraint_row.oid) = 'UNIQUE (external_identity_id)'),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint AS constraint_row
                    WHERE constraint_row.conrelid = relation.oid
                      AND constraint_row.contype = 'f'
                      AND constraint_row.confrelid = 'foundation.external_identities'::regclass),
                has_table_privilege('vinx_runtime', 'foundation.platform_operators', 'SELECT')
            FROM pg_class AS relation
            INNER JOIN pg_namespace AS schema_row ON schema_row.oid = relation.relnamespace
            WHERE schema_row.nspname = 'foundation'
              AND relation.relname = 'platform_operators';

            SELECT
                current_function.prosecdef,
                current_function.provolatile,
                current_function.proconfig,
                has_function_privilege(
                    'vinx_runtime',
                    'foundation.current_actor_is_platform_operator()',
                    'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(
                        current_function.proacl,
                        acldefault('f', current_function.proowner))) AS privilege
                    WHERE privilege.grantee = 0
                      AND privilege.privilege_type = 'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(
                        current_function.proacl,
                        acldefault('f', current_function.proowner))) AS privilege
                    WHERE privilege.privilege_type = 'EXECUTE'
                      AND privilege.grantee <> current_function.proowner
                      AND privilege.grantee NOT IN (
                          SELECT oid
                          FROM pg_roles
                          WHERE rolname IN (
                              'vinx_runtime',
                              'vinx_provisioning_owner',
                              'vinx_employee_directory_owner')))
            FROM pg_proc AS current_function
            WHERE current_function.oid =
                'foundation.current_actor_is_platform_operator()'::regprocedure;

            SELECT
                bootstrap_function.prosecdef,
                pg_get_userbyid(bootstrap_function.proowner),
                bootstrap_function.proconfig,
                has_function_privilege(
                    'vinx_migrator',
                    'foundation.bootstrap_platform_operator(text,text,text,text,uuid)',
                    'EXECUTE'),
                NOT has_function_privilege(
                    'vinx_runtime',
                    'foundation.bootstrap_platform_operator(text,text,text,text,uuid)',
                    'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(
                        bootstrap_function.proacl,
                        acldefault('f', bootstrap_function.proowner))) AS privilege
                    WHERE privilege.grantee = 0
                      AND privilege.privilege_type = 'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(
                        bootstrap_function.proacl,
                        acldefault('f', bootstrap_function.proowner))) AS privilege
                    WHERE privilege.privilege_type = 'EXECUTE'
                      AND privilege.grantee NOT IN (
                          bootstrap_function.proowner,
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_migrator')))
            FROM pg_proc AS bootstrap_function
            WHERE bootstrap_function.oid =
                'foundation.bootstrap_platform_operator(text,text,text,text,uuid)'::regprocedure;

            SELECT
                platform_owner.rolcanlogin,
                platform_owner.rolinherit,
                platform_owner.rolbypassrls,
                has_schema_privilege('vinx_platform_owner', 'foundation', 'USAGE'),
                has_schema_privilege('vinx_platform_owner', 'foundation', 'CREATE'),
                EXISTS (
                    SELECT 1
                    FROM pg_auth_members AS membership
                    INNER JOIN pg_roles AS role ON role.oid = membership.roleid
                    INNER JOIN pg_roles AS member ON member.oid = membership.member
                    WHERE role.rolname = 'vinx_platform_owner'
                      AND member.rolname = 'vinx_migrator'
                      AND NOT membership.inherit_option
                      AND membership.set_option),
                has_column_privilege(
                    'vinx_platform_owner',
                    'foundation.users',
                    'id',
                    'REFERENCES'),
                has_column_privilege(
                    'vinx_platform_owner',
                    'foundation.external_identities',
                    'id',
                    'REFERENCES')
            FROM pg_roles AS platform_owner
            WHERE platform_owner.rolname = 'vinx_platform_owner';
            """,
            administrator
        );

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("singleton", reader.GetString(0));
        Assert.Equal("smallint", reader.GetString(1));
        Assert.Equal("NO", reader.GetString(2));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("external_identity_id", reader.GetString(0));
        Assert.Equal("uuid", reader.GetString(1));
        Assert.Equal("NO", reader.GetString(2));

        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));

        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal('s', reader.GetChar(1));
        Assert.Equal(
            ["search_path=pg_catalog, foundation, pg_temp"],
            reader.GetFieldValue<string[]>(2)
        );
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));

        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("vinx_platform_owner", reader.GetString(1));
        Assert.Equal(
            ["search_path=pg_catalog, foundation, pg_temp"],
            reader.GetFieldValue<string[]>(2)
        );
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));

        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
        Assert.True(reader.GetBoolean(7));
    }

    [Fact]
    public async Task Runtime_can_evaluate_the_current_actor_function_without_direct_table_access()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var operatorUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        Guid operatorIdentityId;

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        {
            operatorIdentityId = await SeedUserAndIdentityAsync(administrator, operatorUserId);
            await using var seed = new NpgsqlCommand(
                "INSERT INTO foundation.platform_operators (singleton, external_identity_id) VALUES (1, @identity_id)",
                administrator
            );
            seed.Parameters.AddWithValue("identity_id", operatorIdentityId);
            await seed.ExecuteNonQueryAsync();
        }

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var transaction = await runtime.BeginTransactionAsync();
        await using (
            var setActor = new NpgsqlCommand(
                """
                SELECT
                    set_config('app.user_id', @user_id, true),
                    set_config('app.external_identity_id', @identity_id, true)
                """,
                runtime,
                transaction
            )
        )
        {
            setActor.Parameters.AddWithValue("user_id", operatorUserId.ToString());
            setActor.Parameters.AddWithValue("identity_id", operatorIdentityId.ToString());
            await setActor.ExecuteNonQueryAsync();
        }

        await using (
            var isOperator = new NpgsqlCommand(
                "SELECT foundation.current_actor_is_platform_operator()",
                runtime,
                transaction
            )
        )
        {
            Assert.True((bool)(await isOperator.ExecuteScalarAsync())!);
        }

        await using (
            var directSelect = new NpgsqlCommand(
                "SELECT count(*) FROM foundation.platform_operators",
                runtime,
                transaction
            )
        )
        {
            await Assert.ThrowsAsync<PostgresException>(() => directSelect.ExecuteScalarAsync());
        }

        await transaction.RollbackAsync();
        await using var otherTransaction = await runtime.BeginTransactionAsync();
        await using var setOtherActor = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @user_id, true)",
            runtime,
            otherTransaction
        );
        setOtherActor.Parameters.AddWithValue("user_id", otherUserId.ToString());
        await setOtherActor.ExecuteNonQueryAsync();
        await using var isOtherOperator = new NpgsqlCommand(
            "SELECT foundation.current_actor_is_platform_operator()",
            runtime,
            otherTransaction
        );
        Assert.False((bool)(await isOtherOperator.ExecuteScalarAsync())!);

        await using (
            var createTempShadow = new NpgsqlCommand(
                "CREATE TEMP TABLE platform_operators (user_id uuid NOT NULL)",
                runtime,
                otherTransaction
            )
        )
        {
            await createTempShadow.ExecuteNonQueryAsync();
        }
        await using (
            var seedTempShadow = new NpgsqlCommand(
                "INSERT INTO platform_operators (user_id) VALUES (@user_id)",
                runtime,
                otherTransaction
            )
        )
        {
            seedTempShadow.Parameters.AddWithValue("user_id", otherUserId);
            await seedTempShadow.ExecuteNonQueryAsync();
        }
        await using var isTempShadowOperator = new NpgsqlCommand(
            "SELECT foundation.current_actor_is_platform_operator()",
            runtime,
            otherTransaction
        );
        Assert.False((bool)(await isTempShadowOperator.ExecuteScalarAsync())!);
        await otherTransaction.RollbackAsync();
    }

    [Fact]
    public async Task Runtime_denies_an_alternate_identity_for_the_same_pinned_operator_user()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var operatorUserId = Guid.NewGuid();
        var alternateIdentityId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        {
            var pinnedIdentityId = await SeedUserAndIdentityAsync(administrator, operatorUserId);
            await using var seedAlternateIdentity = new NpgsqlCommand(
                """
                INSERT INTO foundation.external_identities (id, user_id, issuer, subject, created_at)
                VALUES (
                    @identity_id,
                    @user_id,
                    'https://issuer.platform.test',
                    'operator-alternate-subject',
                    clock_timestamp())
                """,
                administrator
            );
            seedAlternateIdentity.Parameters.AddWithValue("identity_id", alternateIdentityId);
            seedAlternateIdentity.Parameters.AddWithValue("user_id", operatorUserId);
            await seedAlternateIdentity.ExecuteNonQueryAsync();

            await using var seedOperator = new NpgsqlCommand(
                "INSERT INTO foundation.platform_operators (singleton, external_identity_id) VALUES (1, @identity_id)",
                administrator
            );
            seedOperator.Parameters.AddWithValue("identity_id", pinnedIdentityId);
            await seedOperator.ExecuteNonQueryAsync();
        }

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var transaction = await runtime.BeginTransactionAsync();
        await using var setActor = new NpgsqlCommand(
            """
            SELECT
                set_config('app.user_id', @user_id, true),
                set_config('app.external_identity_id', @identity_id, true)
            """,
            runtime,
            transaction
        );
        setActor.Parameters.AddWithValue("user_id", operatorUserId.ToString());
        setActor.Parameters.AddWithValue("identity_id", alternateIdentityId.ToString());
        await setActor.ExecuteNonQueryAsync();

        await using var isOperator = new NpgsqlCommand(
            "SELECT foundation.current_actor_is_platform_operator()",
            runtime,
            transaction
        );
        Assert.False((bool)(await isOperator.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Runtime_cannot_shadow_the_audit_appender_target_with_a_temp_relation()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var correlationId = Guid.NewGuid();

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using (
            var createTempShadow = new NpgsqlCommand(
                """
                CREATE TEMP TABLE audit_events (
                    id uuid,
                    actor_user_id uuid,
                    scope text,
                    requested_company_id uuid,
                    source text,
                    action text,
                    resource_type text,
                    resource_id uuid,
                    outcome text,
                    correlation_id uuid,
                    reason text,
                    metadata_json jsonb,
                    occurred_at timestamp with time zone
                );
                GRANT INSERT ON TABLE audit_events TO vinx_audit_owner;
                """,
                runtime
            )
        )
        {
            await createTempShadow.ExecuteNonQueryAsync();
        }

        await using (
            var append = new NpgsqlCommand(
                """
                SELECT audit.append_event(
                    @actor_user_id,
                    'global',
                    NULL,
                    'worker',
                    'platform.operator.temp_shadow_probe',
                    'platform_operator',
                    NULL,
                    'success',
                    @correlation_id,
                    'migration_test',
                    '{}'::jsonb)
                """,
                runtime
            )
        )
        {
            append.Parameters.AddWithValue("actor_user_id", Guid.NewGuid());
            append.Parameters.AddWithValue("correlation_id", correlationId);
            await append.ExecuteNonQueryAsync();
        }

        await using (
            var shadowCount = new NpgsqlCommand(
                "SELECT count(*) FROM audit_events WHERE correlation_id = @correlation_id",
                runtime
            )
        )
        {
            shadowCount.Parameters.AddWithValue("correlation_id", correlationId);
            Assert.Equal(0L, (long)(await shadowCount.ExecuteScalarAsync())!);
        }

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var durableCount = new NpgsqlCommand(
            "SELECT count(*) FROM audit.audit_events WHERE correlation_id = @correlation_id",
            administrator
        );
        durableCount.Parameters.AddWithValue("correlation_id", correlationId);
        Assert.Equal(1L, (long)(await durableCount.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Downgrade_is_blocked_when_a_platform_operator_is_live()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var operatorUserId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        {
            var operatorIdentityId = await SeedUserAndIdentityAsync(administrator, operatorUserId);
            await using var seed = new NpgsqlCommand(
                "INSERT INTO foundation.platform_operators (singleton, external_identity_id) VALUES (1, @identity_id)",
                administrator
            );
            seed.Parameters.AddWithValue("identity_id", operatorIdentityId);
            await seed.ExecuteNonQueryAsync();
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        var migrations = migrator.Database.GetMigrations().ToArray();
        var migrationIndex = Array.FindIndex(
            migrations,
            id => id.Contains("PlatformOperator", StringComparison.OrdinalIgnoreCase)
        );
        Assert.True(migrationIndex > 0);
        await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.Database.MigrateAsync(migrations[migrationIndex - 1])
        );

        await using var verify = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('foundation.platform_operators')::text, count(*) FROM foundation.platform_operators",
            verify
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("foundation.platform_operators", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    private static async Task<Guid> SeedUserAndIdentityAsync(
        NpgsqlConnection administrator,
        Guid userId
    )
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES (@user_id, 'Platform operator', @email, clock_timestamp())
            """,
            administrator
        );
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("email", $"{userId:N}@example.test");
        await command.ExecuteNonQueryAsync();

        var identityId = Guid.NewGuid();
        await using var identity = new NpgsqlCommand(
            """
            INSERT INTO foundation.external_identities (id, user_id, issuer, subject, created_at)
            VALUES (@identity_id, @user_id, 'https://issuer.platform.test', @subject, clock_timestamp())
            """,
            administrator
        );
        identity.Parameters.AddWithValue("identity_id", identityId);
        identity.Parameters.AddWithValue("user_id", userId);
        identity.Parameters.AddWithValue("subject", $"operator-{userId:N}");
        await identity.ExecuteNonQueryAsync();
        return identityId;
    }
}
