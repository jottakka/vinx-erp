using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class FoundationSchemaTests
{
    private static readonly (
        string Schema,
        string Table,
        string Privilege
    )[] ExpectedRuntimeTableGrants =
    [
        ("foundation", "approvals", "INSERT"),
        ("foundation", "approvals", "SELECT"),
        ("foundation", "capabilities", "SELECT"),
        ("foundation", "companies", "SELECT"),
        ("foundation", "employees", "SELECT"),
        ("foundation", "external_identities", "SELECT"),
        ("foundation", "files", "INSERT"),
        ("foundation", "files", "SELECT"),
        ("foundation", "files", "UPDATE"),
        ("foundation", "groups", "SELECT"),
        ("foundation", "idempotency_records", "INSERT"),
        ("foundation", "idempotency_records", "SELECT"),
        ("foundation", "idempotency_records", "UPDATE"),
        ("foundation", "memberships", "SELECT"),
        ("foundation", "role_assignments", "INSERT"),
        ("foundation", "role_assignments", "SELECT"),
        ("foundation", "role_assignments", "UPDATE"),
        ("foundation", "role_capabilities", "SELECT"),
        ("foundation", "roles", "SELECT"),
        ("foundation", "user_sessions", "INSERT"),
        ("foundation", "user_sessions", "SELECT"),
    ];

    private static readonly string[] ExpectedTables =
    [
        "audit.audit_events",
        "foundation.approvals",
        "foundation.capabilities",
        "foundation.companies",
        "foundation.employees",
        "foundation.external_identities",
        "foundation.files",
        "foundation.groups",
        "foundation.idempotency_records",
        "foundation.memberships",
        "foundation.organization_provisioning_receipts",
        "foundation.pending_owner_invitations",
        "foundation.platform_operators",
        "foundation.role_assignments",
        "foundation.role_capabilities",
        "foundation.roles",
        "foundation.teams",
        "foundation.units",
        "foundation.user_sessions",
        "foundation.users",
    ];

    private static readonly string[] ExpectedRlsTables =
    [
        "approvals",
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
        "units",
    ];

    [Fact]
    public async Task MigrateAsync_applies_the_initial_foundation_schema()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();

        var migrateAsync =
            typeof(FoundationPostgresFixture).GetMethod("MigrateAsync")
            ?? throw new Xunit.Sdk.XunitException(
                "MigrateAsync must migrate the Foundation EF baseline."
            );
        await (
            (Task)(
                migrateAsync.Invoke(fixture, null)
                ?? throw new Xunit.Sdk.XunitException("MigrateAsync must return a task.")
            )
        );

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT to_regclass('foundation.companies') IS NOT NULL",
            connection
        );

        Assert.True(
            (bool)(
                await command.ExecuteScalarAsync()
                ?? throw new Xunit.Sdk.XunitException(
                    "The schema existence query returned no value."
                )
            )
        );
    }

    [Fact]
    public async Task Normalized_name_indexes_are_migration_owned_while_company_group_index_remains_in_the_ef_model()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var context = fixture.CreateAdministratorDbContext();
        Assert.DoesNotContain(
            context.Model.FindEntityType(typeof(Group))!.GetIndexes(),
            index => index.GetDatabaseName() == "ix_groups_name"
        );
        Assert.DoesNotContain(
            context.Model.FindEntityType(typeof(Company))!.GetIndexes(),
            index => index.GetDatabaseName() == "ix_companies_normalized_name"
        );
        Assert.DoesNotContain(
            context.Model.FindEntityType(typeof(User))!.GetIndexes(),
            index => index.GetDatabaseName() == "ix_users_email"
        );
        Assert.Contains(
            context.Model.FindEntityType(typeof(Company))!.GetIndexes(),
            index =>
                index.GetDatabaseName() == "ix_companies_group_id"
                && index
                    .Properties.Select(property => property.Name)
                    .SequenceEqual([nameof(Company.GroupId)])
        );

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT indexname, indexdef
            FROM pg_indexes
            WHERE schemaname = 'foundation'
              AND indexname IN ('ix_groups_name', 'ix_companies_normalized_name', 'ix_users_email')
            ORDER BY indexname
            """,
            connection
        );
        await using var reader = await command.ExecuteReaderAsync();
        var indexes = new List<(string Name, string Definition)>();
        while (await reader.ReadAsync())
        {
            indexes.Add((reader.GetString(0), reader.GetString(1)));
        }

        Assert.Equal(
            ["ix_companies_normalized_name", "ix_groups_name", "ix_users_email"],
            indexes.Select(index => index.Name)
        );
        Assert.All(
            indexes,
            index => Assert.Contains("lower", index.Definition, StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task Capabilities_catalog_uses_rls_to_limit_provisioning_owner_but_preserves_runtime_catalog_reads()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using (
            var rlsCommand = new NpgsqlCommand(
                "SELECT relrowsecurity FROM pg_class WHERE oid = 'foundation.capabilities'::regclass",
                administrator
            )
        )
        {
            Assert.True(
                (bool)(
                    await rlsCommand.ExecuteScalarAsync()
                    ?? throw new Xunit.Sdk.XunitException(
                        "Capabilities RLS query returned no value."
                    )
                )
            );
        }

        var expectedCatalog = await ReadCapabilityNamesAsync(administrator);
        Assert.NotEmpty(expectedCatalog);

        await using var provisioning = await fixture.OpenAdministratorConnectionAsync();
        await using (
            var setRole = new NpgsqlCommand("SET ROLE vinx_provisioning_owner", provisioning)
        )
        {
            await setRole.ExecuteNonQueryAsync();
        }

        try
        {
            var provisioningCatalog = await ReadCapabilityNamesAsync(provisioning);
            Assert.Equal(
                [
                    "foundation.files.confirm",
                    "foundation.files.upload",
                    "foundation.memberships.revoke",
                    "foundation.roles.assign",
                ],
                provisioningCatalog
            );
        }
        finally
        {
            await using var resetRole = new NpgsqlCommand("RESET ROLE", provisioning);
            await resetRole.ExecuteNonQueryAsync();
        }

        await using var runtime = await fixture.OpenAdministratorConnectionAsync();
        await using (var setRole = new NpgsqlCommand("SET ROLE vinx_runtime", runtime))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        try
        {
            Assert.Equal(expectedCatalog, await ReadCapabilityNamesAsync(runtime));
        }
        finally
        {
            await using var resetRole = new NpgsqlCommand("RESET ROLE", runtime);
            await resetRole.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string[]> ReadCapabilityNamesAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "SELECT name FROM foundation.capabilities ORDER BY name",
            connection
        );
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
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
            """,
            connection
        );
        await using var directGrantsReader = await directGrantsCommand.ExecuteReaderAsync();
        var directGrants = new List<(string Schema, string Table, string Privilege)>();
        while (await directGrantsReader.ReadAsync())
        {
            directGrants.Add(
                (
                    directGrantsReader.GetString(0),
                    directGrantsReader.GetString(1),
                    directGrantsReader.GetString(2)
                )
            );
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
            """,
            connection
        );
        await using var effectivePrivilegesReader =
            await effectivePrivilegesCommand.ExecuteReaderAsync();
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
                hasPrivilege
            );
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
                    'audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)',
                    'EXECUTE'),
                has_function_privilege(
                    'vinx_runtime',
                    'audit.reject_audit_event_mutation()',
                    'EXECUTE'),
                has_function_privilege(
                    'vinx_runtime',
                    'foundation.lock_user_for_session_admission(uuid)',
                    'EXECUTE')
            """,
            connection
        );
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
        Assert.True(reader.GetBoolean(10));
    }

    [Fact]
    public async Task Audit_events_require_a_scope_without_a_database_default()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT attribute.attnotnull, definition.adbin IS NULL
            FROM pg_attribute AS attribute
            LEFT JOIN pg_attrdef AS definition
                ON definition.adrelid = attribute.attrelid
                AND definition.adnum = attribute.attnum
            WHERE attribute.attrelid = 'audit.audit_events'::regclass
              AND attribute.attname = 'scope'
            """,
            connection
        );
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
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
            """,
            connection
        );
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
            SELECT tablename, permissive, cmd, roles, qual, with_check
            FROM pg_policies
            WHERE schemaname = 'foundation'
              AND tablename = ANY (@table_names)
              AND policyname = tablename || '_company_isolation'
            ORDER BY tablename
            """,
            connection
        );
        policyCommand.Parameters.AddWithValue("table_names", ExpectedRlsTables);
        await using var policyReader = await policyCommand.ExecuteReaderAsync();
        var policyTables = new List<string>();
        while (await policyReader.ReadAsync())
        {
            policyTables.Add(policyReader.GetString(0));
            Assert.Equal("PERMISSIVE", policyReader.GetString(1));
            Assert.Equal("ALL", policyReader.GetString(2));
            Assert.Equal(["public"], policyReader.GetFieldValue<string[]>(3));
            AssertFailClosedCompanySetting(policyReader.GetString(4));
            AssertFailClosedCompanySetting(policyReader.GetString(5));
        }
        Assert.Equal(ExpectedRlsTables, policyTables);
    }

    [Fact]
    public async Task Runtime_employee_read_grant_remains_bounded_by_the_authorized_company_policy()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT cmd, roles, qual
            FROM pg_policies
            WHERE schemaname = 'foundation'
              AND tablename = 'employees'
              AND policyname = 'employees_authorized_company_read'
            """,
            connection
        );
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("SELECT", reader.GetString(0));
        Assert.Equal(["public"], reader.GetFieldValue<string[]>(1));
        var policyExpression = reader.GetString(2);
        AssertFailClosedCompanySetting(policyExpression);
        Assert.Contains(
            "foundation.current_authorized_company_ids()",
            policyExpression,
            StringComparison.Ordinal
        );
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
            """,
            administrator
        );
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
        var capability = await administrator.Capabilities.SingleAsync(candidate =>
            candidate.Name == KnownCapabilities.RolesAssign
        );
        var invalidGrant = new RoleCapability(
            Guid.NewGuid(),
            company.Id,
            role.Id,
            capability.Id,
            (CapabilityScope)999,
            actorId,
            requestId
        );

        administrator.AddRange(group, company, role, invalidGrant);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await administrator.SaveChangesAsync()
        );
        Assert.IsType<ArgumentOutOfRangeException>(exception.InnerException);
    }

    private static void AssertFailClosedCompanySetting(string policyExpression)
    {
        Assert.Contains(
            "current_setting('app.company_id'::text, true)",
            policyExpression,
            StringComparison.Ordinal
        );
        Assert.Contains("NULLIF", policyExpression, StringComparison.Ordinal);
        Assert.Contains("::uuid", policyExpression, StringComparison.Ordinal);
    }
}
