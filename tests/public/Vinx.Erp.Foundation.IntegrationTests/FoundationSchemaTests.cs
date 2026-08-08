using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Npgsql;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class FoundationSchemaTests
{
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
    public async Task Runtime_grants_only_the_foundation_operations_required_by_the_baseline()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                has_database_privilege('vinx_runtime', current_database(), 'CONNECT'),
                has_schema_privilege('vinx_runtime', 'foundation', 'USAGE'),
                has_schema_privilege('vinx_runtime', 'audit', 'USAGE'),
                has_table_privilege('vinx_runtime', 'foundation.companies', 'SELECT'),
                has_table_privilege('vinx_runtime', 'foundation.memberships', 'SELECT'),
                has_table_privilege('vinx_runtime', 'foundation.roles', 'SELECT'),
                has_table_privilege('vinx_runtime', 'foundation.role_capabilities', 'SELECT'),
                has_table_privilege('vinx_runtime', 'foundation.role_assignments', 'INSERT'),
                has_table_privilege('vinx_runtime', 'foundation.idempotency_records', 'UPDATE'),
                has_table_privilege('vinx_runtime', 'foundation.files', 'SELECT'),
                has_table_privilege('vinx_runtime', 'foundation.files', 'INSERT'),
                has_table_privilege('vinx_runtime', 'foundation.capabilities', 'SELECT'),
                has_table_privilege('vinx_runtime', 'foundation.capabilities', 'INSERT'),
                has_table_privilege('vinx_runtime', 'foundation.users', 'SELECT'),
                has_table_privilege('vinx_runtime', 'foundation.external_identities', 'SELECT'),
                has_table_privilege('vinx_runtime', 'audit.audit_events', 'INSERT'),
                has_function_privilege('vinx_runtime', 'audit.append_event(uuid,uuid,text,text,text,uuid,text,uuid,text,jsonb)', 'EXECUTE')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
        Assert.True(reader.GetBoolean(7));
        Assert.True(reader.GetBoolean(8));
        Assert.True(reader.GetBoolean(9));
        Assert.False(reader.GetBoolean(10));
        Assert.True(reader.GetBoolean(11));
        Assert.False(reader.GetBoolean(12));
        Assert.False(reader.GetBoolean(13));
        Assert.False(reader.GetBoolean(14));
        Assert.False(reader.GetBoolean(15));
        Assert.True(reader.GetBoolean(16));
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
            "SELECT to_regclass('foundation.companies') IS NULL", administrator);
        Assert.True((bool)(await command.ExecuteScalarAsync()
            ?? throw new Xunit.Sdk.XunitException("The schema existence query returned no value.")));
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
}
