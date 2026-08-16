using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class EmployeeDirectoryMigrationTests
{
    private const string BeforeEmployeeDirectory =
        "20260816012000_AddPlatformOrganizationProvisioning";

    [Fact]
    public async Task Empty_state_downgrade_removes_only_employee_directory_contracts()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using (var beforeDowngrade = await fixture.OpenAdministratorConnectionAsync())
        await using (
            var capability = new NpgsqlCommand(
                "SELECT id FROM foundation.capabilities WHERE name = 'foundation.employees.create'",
                beforeDowngrade
            )
        )
        {
            Assert.Equal(
                KnownCapabilities.EmployeesCreateId,
                await capability.ExecuteScalarAsync()
            );
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeEmployeeDirectory);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regprocedure('foundation.create_employee_directory_entry(uuid,uuid,text,text,text)') IS NULL,
                to_regprocedure('foundation.ensure_platform_owner_employee_create(uuid,uuid,uuid)') IS NULL,
                NOT EXISTS (
                    SELECT 1
                    FROM foundation.capabilities
                    WHERE name = 'foundation.employees.create'),
                NOT has_table_privilege('vinx_runtime', 'foundation.employees', 'SELECT'),
                to_regprocedure('foundation.provision_platform_organization(uuid,text,text,text,text,text)') IS NOT NULL
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
    }

    [Fact]
    public async Task Downgrade_fails_closed_after_employee_directory_state_exists()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var actorId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (
            var seed = new NpgsqlCommand(
                """
                INSERT INTO foundation.users (id, display_name, email, created_at)
                VALUES (@actor_id, 'Actor', @email, clock_timestamp());
                INSERT INTO foundation.groups (
                    id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (
                    @group_id, @group_name, @actor_id, @actor_id, @request_id,
                    clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.companies (
                    id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (
                    @company_id, @group_id, @company_name, @actor_id, @actor_id, @request_id,
                    clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.idempotency_records (
                    id, company_id, actor_user_id, command_name, request_id, payload_hash, result_json, created_at)
                VALUES (
                    gen_random_uuid(), @company_id, @actor_id, 'foundation.employee.create', @request_id,
                    repeat('a', 64), '{}'::jsonb, clock_timestamp());
                """,
                administrator
            )
        )
        {
            seed.Parameters.AddWithValue("actor_id", actorId);
            seed.Parameters.AddWithValue("email", $"actor-{actorId:N}@example.test");
            seed.Parameters.AddWithValue("group_id", groupId);
            seed.Parameters.AddWithValue("group_name", $"Group {groupId:N}");
            seed.Parameters.AddWithValue("company_id", companyId);
            seed.Parameters.AddWithValue("company_name", $"Company {companyId:N}");
            seed.Parameters.AddWithValue("request_id", requestId);
            await seed.ExecuteNonQueryAsync();
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync(BeforeEmployeeDirectory)
        );

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        await using var verify = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regprocedure('foundation.create_employee_directory_entry(uuid,uuid,text,text,text)') IS NOT NULL,
                EXISTS (
                    SELECT 1
                    FROM "__EFMigrationsHistory"
                    WHERE migration_id LIKE '%_AddEmployeeDirectory')
            """,
            verify
        );
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
    }

    [Fact]
    public async Task Downgrade_fails_closed_for_a_live_legacy_named_employee_capability_grant()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeEmployeeDirectory);
        var actorId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var capabilityId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (
            var seed = new NpgsqlCommand(
                """
                INSERT INTO foundation.users (id, display_name, email, created_at) VALUES (@actor, 'Actor', @email, clock_timestamp());
                INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at) VALUES (@group, @group_name, @actor, @actor, @request, clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at) VALUES (@company, @group, @company_name, @actor, @actor, @request, clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.roles (id, company_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at) VALUES (@role, @company, 'Legacy', @actor, @actor, @request, clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.capabilities (id, name, created_at) VALUES (@capability, 'foundation.employees.create', clock_timestamp());
                INSERT INTO foundation.role_capabilities (id, company_id, role_id, capability_id, scope, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at) VALUES (gen_random_uuid(), @company, @role, @capability, 'company', @actor, @actor, @request, clock_timestamp(), clock_timestamp());
                """,
                administrator
            )
        )
        {
            seed.Parameters.AddWithValue("actor", actorId);
            seed.Parameters.AddWithValue("email", $"actor-{actorId:N}@example.test");
            seed.Parameters.AddWithValue("group", groupId);
            seed.Parameters.AddWithValue("group_name", $"Group {groupId:N}");
            seed.Parameters.AddWithValue("company", companyId);
            seed.Parameters.AddWithValue("company_name", $"Company {companyId:N}");
            seed.Parameters.AddWithValue("role", roleId);
            seed.Parameters.AddWithValue("capability", capabilityId);
            seed.Parameters.AddWithValue("request", requestId);
            await seed.ExecuteNonQueryAsync();
        }
        await migrator.Database.MigrateAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.Database.MigrateAsync(BeforeEmployeeDirectory)
        );
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Downgrade_fails_closed_when_the_employee_capability_has_an_active_grant()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var actorId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (
            var seed = new NpgsqlCommand(
                """
                INSERT INTO foundation.users (id, display_name, email, created_at)
                VALUES (@actor_id, 'Actor', @email, clock_timestamp());
                INSERT INTO foundation.groups (
                    id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (
                    @group_id, @group_name, @actor_id, @actor_id, @request_id,
                    clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.companies (
                    id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (
                    @company_id, @group_id, @company_name, @actor_id, @actor_id, @request_id,
                    clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.roles (
                    id, company_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (
                    @role_id, @company_id, 'Owner', @actor_id, @actor_id, @request_id,
                    clock_timestamp(), clock_timestamp());
                INSERT INTO foundation.role_capabilities (
                    id, company_id, role_id, capability_id, scope,
                    created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (
                    gen_random_uuid(), @company_id, @role_id, @capability_id, 'company',
                    @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
                """,
                administrator
            )
        )
        {
            seed.Parameters.AddWithValue("actor_id", actorId);
            seed.Parameters.AddWithValue("email", $"actor-{actorId:N}@example.test");
            seed.Parameters.AddWithValue("group_id", groupId);
            seed.Parameters.AddWithValue("group_name", $"Group {groupId:N}");
            seed.Parameters.AddWithValue("company_id", companyId);
            seed.Parameters.AddWithValue("company_name", $"Company {companyId:N}");
            seed.Parameters.AddWithValue("role_id", roleId);
            seed.Parameters.AddWithValue("capability_id", KnownCapabilities.EmployeesCreateId);
            seed.Parameters.AddWithValue("request_id", requestId);
            await seed.ExecuteNonQueryAsync();
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync(BeforeEmployeeDirectory)
        );

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(
            "cannot downgrade employee directory while state exists",
            exception.MessageText
        );
    }
}
