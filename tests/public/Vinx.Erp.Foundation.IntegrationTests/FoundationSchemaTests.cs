using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Npgsql;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class FoundationSchemaTests
{
    private static readonly (string Schema, string Table, string Privilege)[] ExpectedRuntimeTableGrants =
    [
        ("foundation", "capabilities", "SELECT"),
        ("foundation", "companies", "SELECT"),
        ("foundation", "files", "SELECT"),
        ("foundation", "groups", "SELECT"),
        ("foundation", "idempotency_records", "INSERT"),
        ("foundation", "idempotency_records", "SELECT"),
        ("foundation", "idempotency_records", "UPDATE"),
        ("foundation", "memberships", "SELECT"),
        ("foundation", "role_assignments", "INSERT"),
        ("foundation", "role_assignments", "SELECT"),
        ("foundation", "role_assignments", "UPDATE"),
        ("foundation", "role_capabilities", "SELECT"),
        ("foundation", "roles", "SELECT")
    ];

    private static readonly string[] ExpectedTables =
    [
        "audit.audit_events",
        "foundation.capabilities",
        "foundation.companies",
        "foundation.employees",
        "foundation.external_identities",
        "foundation.files",
        "foundation.groups",
        "foundation.idempotency_records",
        "foundation.memberships",
        "foundation.role_assignments",
        "foundation.role_capabilities",
        "foundation.roles",
        "foundation.teams",
        "foundation.units",
        "foundation.users"
    ];

    private static readonly string[] ExpectedRlsTables =
    [
        "companies",
        "employees",
        "files",
        "groups",
        "idempotency_records",
        "memberships",
        "role_assignments",
        "role_capabilities",
        "roles",
        "teams",
        "units"
    ];

    [Fact]
    public async Task MigrateAsync_applies_the_initial_foundation_schema()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();

        var migrateAsync = typeof(FoundationPostgresFixture).GetMethod("MigrateAsync")
            ?? throw new Xunit.Sdk.XunitException("MigrateAsync must migrate the Foundation EF baseline.");
        await ((Task)(migrateAsync.Invoke(fixture, null)
            ?? throw new Xunit.Sdk.XunitException("MigrateAsync must return a task.")));

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT to_regclass('foundation.companies') IS NOT NULL", connection);

        Assert.True((bool)(await command.ExecuteScalarAsync()
            ?? throw new Xunit.Sdk.XunitException("The schema existence query returned no value.")));
    }

    [Fact]
    public async Task Runtime_direct_and_effective_table_privileges_match_the_complete_baseline_matrix()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var directGrantsCommand = new NpgsqlCommand(
            """
            SELECT table_schema, table_name, privilege_type
            FROM information_schema.role_table_grants
            WHERE grantee = 'vinx_runtime'
              AND table_schema IN ('foundation', 'audit')
            ORDER BY table_schema, table_name, privilege_type
            """, connection);
        await using var directGrantsReader = await directGrantsCommand.ExecuteReaderAsync();
        var directGrants = new List<(string Schema, string Table, string Privilege)>();
        while (await directGrantsReader.ReadAsync())
        {
            directGrants.Add((
                directGrantsReader.GetString(0),
                directGrantsReader.GetString(1),
                directGrantsReader.GetString(2)));
        }
        Assert.Equal(ExpectedRuntimeTableGrants, directGrants);
        await directGrantsReader.CloseAsync();

        await using var effectivePrivilegesCommand = new NpgsqlCommand(
            """
            SELECT
                table_schema,
                table_name,
                privilege.privilege_type,
                has_table_privilege(
                    'vinx_runtime',
                    format('%I.%I', table_schema, table_name),
                    privilege.privilege_type)
            FROM information_schema.tables
            CROSS JOIN (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE')) AS privilege(privilege_type)
            WHERE table_schema IN ('foundation', 'audit')
              AND table_type = 'BASE TABLE'
            ORDER BY table_schema, table_name, privilege.privilege_type
            """, connection);
        await using var effectivePrivilegesReader = await effectivePrivilegesCommand.ExecuteReaderAsync();
        var discoveredTables = new HashSet<string>(StringComparer.Ordinal);
        while (await effectivePrivilegesReader.ReadAsync())
        {
            var schema = effectivePrivilegesReader.GetString(0);
            var table = effectivePrivilegesReader.GetString(1);
            var privilege = effectivePrivilegesReader.GetString(2);
            var hasPrivilege = effectivePrivilegesReader.GetBoolean(3);
            discoveredTables.Add($"{schema}.{table}");
            Assert.Equal(
                ExpectedRuntimeTableGrants.Contains((schema, table, privilege)),
                hasPrivilege);
        }

        Assert.Equal(ExpectedTables, discoveredTables.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Runtime_database_schema_and_function_privileges_match_the_baseline()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                has_database_privilege('vinx_runtime', current_database(), 'CONNECT'),
                has_database_privilege('vinx_runtime', current_database(), 'CREATE'),
                has_schema_privilege('vinx_runtime', 'foundation', 'USAGE'),
                has_schema_privilege('vinx_runtime', 'foundation', 'CREATE'),
                has_schema_privilege('vinx_runtime', 'audit', 'USAGE'),
                has_schema_privilege('vinx_runtime', 'audit', 'CREATE'),
                has_schema_privilege('vinx_runtime', 'public', 'USAGE'),
                has_schema_privilege('vinx_runtime', 'public', 'CREATE'),
                has_function_privilege(
                    'vinx_runtime',
                    'audit.append_event(uuid,uuid,text,text,text,uuid,text,uuid,text,jsonb)',
                    'EXECUTE'),
                has_function_privilege(
                    'vinx_runtime',
                    'audit.reject_audit_event_mutation()',
                    'EXECUTE')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
        Assert.True(reader.GetBoolean(8));
        Assert.False(reader.GetBoolean(9));
    }

    [Fact]
    public async Task Every_company_owned_table_forces_rls_with_a_fail_closed_company_policy()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var tableCommand = new NpgsqlCommand(
            """
            SELECT relation.relname, relation.relrowsecurity, relation.relforcerowsecurity
            FROM pg_class AS relation
            INNER JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
            WHERE schema.nspname = 'foundation'
              AND relation.relname = ANY (@table_names)
            ORDER BY relation.relname
            """, connection);
        tableCommand.Parameters.AddWithValue("table_names", ExpectedRlsTables);
        await using var tableReader = await tableCommand.ExecuteReaderAsync();
        var securedTables = new List<string>();
        while (await tableReader.ReadAsync())
        {
            securedTables.Add(tableReader.GetString(0));
            Assert.True(tableReader.GetBoolean(1));
            Assert.True(tableReader.GetBoolean(2));
        }
        Assert.Equal(ExpectedRlsTables, securedTables);
        await tableReader.CloseAsync();

        await using var policyCommand = new NpgsqlCommand(
            """
            SELECT tablename, qual, with_check
            FROM pg_policies
            WHERE schemaname = 'foundation'
              AND tablename = ANY (@table_names)
            ORDER BY tablename
            """, connection);
        policyCommand.Parameters.AddWithValue("table_names", ExpectedRlsTables);
        await using var policyReader = await policyCommand.ExecuteReaderAsync();
        var policyTables = new List<string>();
        while (await policyReader.ReadAsync())
        {
            policyTables.Add(policyReader.GetString(0));
            AssertFailClosedCompanySetting(policyReader.GetString(1));
            AssertFailClosedCompanySetting(policyReader.GetString(2));
        }
        Assert.Equal(ExpectedRlsTables, policyTables);
    }

    [Fact]
    public async Task Migrator_can_rollback_the_initial_foundation_baseline()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync("0");

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regclass('foundation.companies') IS NULL,
                to_regnamespace('foundation') IS NULL,
                to_regnamespace('audit') IS NULL
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public async Task Invalid_capability_scope_is_rejected_instead_of_becoming_a_group_scope()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using var administrator = fixture.CreateAdministratorDbContext();

        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", actorId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Obras", actorId, requestId);
        var role = new Role(Guid.NewGuid(), company.Id, "Operator", actorId, requestId);
        var capability = new Capability(Guid.NewGuid(), "foundation.roles.assign");
        var invalidGrant = new RoleCapability(
            Guid.NewGuid(),
            company.Id,
            role.Id,
            capability.Id,
            (CapabilityScope)999,
            actorId,
            requestId);

        administrator.AddRange(group, company, role, capability, invalidGrant);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await administrator.SaveChangesAsync());
        Assert.IsType<ArgumentOutOfRangeException>(exception.InnerException);
    }

    private static void AssertFailClosedCompanySetting(string policyExpression)
    {
        Assert.Contains("current_setting('app.company_id'::text, true)", policyExpression, StringComparison.Ordinal);
        Assert.Contains("NULLIF", policyExpression, StringComparison.Ordinal);
        Assert.Contains("::uuid", policyExpression, StringComparison.Ordinal);
    }
}
