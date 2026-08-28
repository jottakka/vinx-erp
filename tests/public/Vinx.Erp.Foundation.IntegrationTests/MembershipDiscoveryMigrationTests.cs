using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class MembershipDiscoveryMigrationTests
{
    private const string BeforeMembershipDiscovery = "20260812032931_AddFoundationCapabilityCatalog";

    [Fact]
    public async Task Migration_exposes_active_actor_memberships_only_through_the_authorization_owner_function()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var definition = new NpgsqlCommand(
            """
            SELECT
                function.prosecdef,
                function.proconfig,
                pg_get_userbyid(function.proowner),
                has_function_privilege(
                    'vinx_runtime',
                    'foundation.current_actor_memberships()',
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
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_runtime'),
                          -- Inbox #94: as funções SECURITY DEFINER de notificação
                          -- consultam memberships ativas via esta função.
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_notifications_owner'),
                          -- O worker do agente reautoriza o ator antes de processar anexos.
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_worker'))),
                authorization_owner.rolcanlogin,
                authorization_owner.rolinherit,
                authorization_owner.rolbypassrls,
                has_schema_privilege('vinx_authorization_owner', 'foundation', 'USAGE'),
                has_schema_privilege('vinx_authorization_owner', 'foundation', 'CREATE'),
                has_table_privilege('vinx_authorization_owner', 'foundation.memberships', 'SELECT'),
                has_table_privilege('vinx_authorization_owner', 'foundation.companies', 'SELECT'),
                NOT pg_has_role('vinx_runtime', 'vinx_authorization_owner', 'MEMBER'),
                EXISTS (
                    SELECT 1
                    FROM pg_auth_members AS membership
                    INNER JOIN pg_roles AS role ON role.oid = membership.roleid
                    INNER JOIN pg_roles AS member ON member.oid = membership.member
                    WHERE role.rolname = 'vinx_authorization_owner'
                      AND member.rolname = 'vinx_migrator'
                      AND NOT membership.inherit_option
                      AND membership.set_option)
            FROM pg_proc AS function
            CROSS JOIN pg_roles AS authorization_owner
            WHERE function.oid = 'foundation.current_actor_memberships()'::regprocedure
              AND authorization_owner.rolname = 'vinx_authorization_owner'
            """, administrator);
        await using var reader = await definition.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal(["search_path=foundation, pg_catalog"], reader.GetFieldValue<string[]>(1));
        Assert.Equal("vinx_authorization_owner", reader.GetString(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
        Assert.False(reader.GetBoolean(8));
        Assert.True(reader.GetBoolean(9));
        Assert.False(reader.GetBoolean(10));
        Assert.True(reader.GetBoolean(11));
        Assert.True(reader.GetBoolean(12));
        Assert.True(reader.GetBoolean(13));
        Assert.True(reader.GetBoolean(14));
    }

    [Fact]
    public async Task Migration_creates_exactly_two_select_only_authorization_owner_policies()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT tablename, policyname, permissive, cmd, roles, qual, with_check
            FROM pg_policies
            WHERE schemaname = 'foundation'
              AND roles @> ARRAY['vinx_authorization_owner']::name[]
            ORDER BY tablename, policyname
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();
        var policies = new List<(string Table, string Name, string Permissive, string Command, string[] Roles, string Qual, string? WithCheck)>();
        while (await reader.ReadAsync())
        {
            policies.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<string[]>(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        Assert.Collection(
            policies,
            policy => AssertAuthorizationOwnerPolicy(
                policy,
                "companies",
                "companies_authorization_owner_read"),
            policy => AssertAuthorizationOwnerPolicy(
                policy,
                "memberships",
                "memberships_authorization_owner_read"));

        await reader.CloseAsync();
        await using var memberships = new NpgsqlCommand(
            """
            SELECT member.rolname, membership.inherit_option, membership.set_option
            FROM pg_auth_members AS membership
            INNER JOIN pg_roles AS role ON role.oid = membership.roleid
            INNER JOIN pg_roles AS member ON member.oid = membership.member
            WHERE role.rolname = 'vinx_authorization_owner'
            ORDER BY member.rolname
            """, administrator);
        await using var membershipReader = await memberships.ExecuteReaderAsync();
        var directMembers = new List<(string Member, bool Inherit, bool Set)>();
        while (await membershipReader.ReadAsync())
        {
            directMembers.Add((
                membershipReader.GetString(0),
                membershipReader.GetBoolean(1),
                membershipReader.GetBoolean(2)));
        }

        Assert.Equal([("vinx_migrator", false, true)], directMembers);
    }

    [Fact]
    public async Task Runtime_membership_discovery_uses_the_transaction_local_actor_and_returns_only_active_rows_in_name_order()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var actorId = Guid.NewGuid();
        var otherActorId = Guid.NewGuid();
        var alfaCompanyId = Guid.NewGuid();
        var betaCompanyId = Guid.NewGuid();
        var deletedCompanyId = Guid.NewGuid();
        var otherCompanyId = Guid.NewGuid();
        var alfaMembershipId = Guid.NewGuid();
        var betaMembershipId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var seed = new NpgsqlCommand(
            """
            INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@group_id, 'Grupo', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES
                (@actor_id, 'Actor', 'actor-memberships@example.test', clock_timestamp()),
                (@other_actor_id, 'Other actor', 'other-memberships@example.test', clock_timestamp());
            INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at, deleted_at)
            VALUES
                (@alfa_company_id, @group_id, 'Alfa', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp(), NULL),
                (@beta_company_id, @group_id, 'Beta', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp(), NULL),
                (@deleted_company_id, @group_id, 'Zeta', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp(), clock_timestamp()),
                (@other_company_id, @group_id, 'Aardvark', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp(), NULL);
            INSERT INTO foundation.memberships (id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at, deleted_at)
            VALUES
                (@alfa_membership_id, @alfa_company_id, @actor_id, @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp(), NULL),
                (@beta_membership_id, @beta_company_id, @actor_id, @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp(), NULL),
                (gen_random_uuid(), @deleted_company_id, @actor_id, @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp(), NULL),
                (gen_random_uuid(), @other_company_id, @other_actor_id, @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp(), NULL);
            """, administrator))
        {
            seed.Parameters.AddWithValue("group_id", Guid.NewGuid());
            seed.Parameters.AddWithValue("actor_id", actorId);
            seed.Parameters.AddWithValue("other_actor_id", otherActorId);
            seed.Parameters.AddWithValue("request_id", Guid.NewGuid());
            seed.Parameters.AddWithValue("alfa_company_id", alfaCompanyId);
            seed.Parameters.AddWithValue("beta_company_id", betaCompanyId);
            seed.Parameters.AddWithValue("deleted_company_id", deletedCompanyId);
            seed.Parameters.AddWithValue("other_company_id", otherCompanyId);
            seed.Parameters.AddWithValue("alfa_membership_id", alfaMembershipId);
            seed.Parameters.AddWithValue("beta_membership_id", betaMembershipId);
            await seed.ExecuteNonQueryAsync();
        }

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using (var transaction = await runtime.BeginTransactionAsync())
        {
            await using var setActor = new NpgsqlCommand(
                "SELECT set_config('app.user_id', @actor_id, true)", runtime, transaction);
            setActor.Parameters.AddWithValue("actor_id", actorId.ToString());
            await setActor.ExecuteNonQueryAsync();

            await using var discover = new NpgsqlCommand(
                "SELECT membership_id, company_id, company_name FROM foundation.current_actor_memberships()",
                runtime,
                transaction);
            await using var discovered = await discover.ExecuteReaderAsync();
            var memberships = new List<(Guid MembershipId, Guid CompanyId, string CompanyName)>();
            while (await discovered.ReadAsync())
            {
                memberships.Add((discovered.GetGuid(0), discovered.GetGuid(1), discovered.GetString(2)));
            }

            Assert.Equal(
            [
                (alfaMembershipId, alfaCompanyId, "Alfa"),
                (betaMembershipId, betaCompanyId, "Beta")
            ],
            memberships);
        }

        await using var noActor = new NpgsqlCommand(
            "SELECT count(*) FROM foundation.current_actor_memberships()", runtime);
        Assert.Equal(0L, await noActor.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Empty_state_downgrade_removes_membership_discovery_and_authorization_owner_access()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeMembershipDiscovery);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regprocedure('foundation.current_actor_memberships()') IS NULL,
                NOT has_table_privilege('vinx_authorization_owner', 'foundation.memberships', 'SELECT'),
                NOT has_table_privilege('vinx_authorization_owner', 'foundation.companies', 'SELECT'),
                NOT EXISTS (
                    SELECT 1
                    FROM pg_policy
                    WHERE polrelid IN ('foundation.memberships'::regclass, 'foundation.companies'::regclass)
                      AND polname IN (
                          'memberships_authorization_owner_read',
                          'companies_authorization_owner_read'))
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
    }

    private static void AssertAuthorizationOwnerPolicy(
        (string Table, string Name, string Permissive, string Command, string[] Roles, string Qual, string? WithCheck) policy,
        string table,
        string name)
    {
        Assert.Equal(table, policy.Table);
        Assert.Equal(name, policy.Name);
        Assert.Equal("PERMISSIVE", policy.Permissive);
        Assert.Equal("SELECT", policy.Command);
        Assert.Equal(["vinx_authorization_owner"], policy.Roles);
        Assert.Equal("true", policy.Qual);
        Assert.Null(policy.WithCheck);
    }
}
