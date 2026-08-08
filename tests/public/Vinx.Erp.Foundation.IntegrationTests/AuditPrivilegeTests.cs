using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AuditPrivilegeTests
{
    [Fact]
    public async Task Runtime_role_cannot_update_or_delete_audit_events()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var auditEventId = await SeedAuditEventAsync(fixture);

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();

        await using var update = new NpgsqlCommand(
            "UPDATE audit.audit_events SET action = 'changed' WHERE id = @id", runtime);
        update.Parameters.AddWithValue("id", auditEventId);
        var updateException = await Assert.ThrowsAsync<PostgresException>(async () =>
            await update.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, updateException.SqlState);

        await using var delete = new NpgsqlCommand(
            "DELETE FROM audit.audit_events WHERE id = @id", runtime);
        delete.Parameters.AddWithValue("id", auditEventId);
        var deleteException = await Assert.ThrowsAsync<PostgresException>(async () =>
            await delete.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleteException.SqlState);
    }

    [Fact]
    public async Task Audit_objects_are_owned_by_the_dedicated_owner_and_runtime_can_only_execute_the_appender()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid = 'audit.audit_events'::regclass)),
                pg_get_userbyid((SELECT proowner FROM pg_proc WHERE oid = 'audit.append_event(uuid,uuid,text,text,text,uuid,text,uuid,text,jsonb)'::regprocedure)),
                has_table_privilege('vinx_runtime', 'audit.audit_events', 'SELECT'),
                has_table_privilege('vinx_runtime', 'audit.audit_events', 'INSERT'),
                has_table_privilege('vinx_runtime', 'audit.audit_events', 'UPDATE'),
                has_table_privilege('vinx_runtime', 'audit.audit_events', 'DELETE'),
                has_function_privilege('vinx_runtime', 'audit.append_event(uuid,uuid,text,text,text,uuid,text,uuid,text,jsonb)', 'EXECUTE'),
                has_function_privilege('vinx_runtime', 'audit.reject_audit_event_mutation()', 'EXECUTE'),
                appender.prosecdef,
                appender.proconfig,
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(appender.proacl, acldefault('f', appender.proowner))) AS privilege
                    WHERE privilege.grantee = 0 AND privilege.privilege_type = 'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM pg_proc AS rejector
                    CROSS JOIN LATERAL aclexplode(
                        COALESCE(rejector.proacl, acldefault('f', rejector.proowner))) AS privilege
                    WHERE rejector.oid = 'audit.reject_audit_event_mutation()'::regprocedure
                      AND privilege.grantee = 0
                      AND privilege.privilege_type = 'EXECUTE')
            FROM pg_proc AS appender
            WHERE appender.oid = 'audit.append_event(uuid,uuid,text,text,text,uuid,text,uuid,text,jsonb)'::regprocedure
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("vinx_audit_owner", reader.GetString(0));
        Assert.Equal("vinx_audit_owner", reader.GetString(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
        Assert.True(reader.GetBoolean(8));
        Assert.Equal(["search_path=audit, pg_catalog"], reader.GetFieldValue<string[]>(9));
        Assert.True(reader.GetBoolean(10));
        Assert.True(reader.GetBoolean(11));
    }

    [Theory]
    [InlineData("company_context_required", AuditOutcome.Success)]
    [InlineData("another_action", AuditOutcome.Denied)]
    public void Null_requested_company_is_rejected_unless_action_and_outcome_are_the_context_required_denial(
        string action,
        AuditOutcome outcome)
    {
        Assert.Throws<ArgumentException>(() => new AuditEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            AuditSource.Web,
            action,
            "authorization",
            null,
            outcome,
            Guid.NewGuid(),
            "redacted",
            "{}"));
    }

    [Fact]
    public void Null_requested_company_is_allowed_for_the_context_required_denial()
    {
        var auditEvent = new AuditEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            AuditSource.Web,
            "company_context_required",
            "authorization",
            null,
            AuditOutcome.Denied,
            Guid.NewGuid(),
            "redacted",
            "{}");

        Assert.Null(auditEvent.RequestedCompanyId);
    }

    [Fact]
    public async Task Runtime_role_can_append_an_audit_event_only_through_the_security_definer_function()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        var actorId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var append = new NpgsqlCommand(
            """
            SELECT audit.append_event(
                @actor_user_id,
                @requested_company_id,
                @source,
                @action,
                @resource_type,
                @resource_id,
                @outcome,
                @correlation_id,
                @reason,
                @metadata)
            """, runtime);
        append.Parameters.AddWithValue("actor_user_id", actorId);
        append.Parameters.AddWithValue("requested_company_id", Guid.NewGuid());
        append.Parameters.AddWithValue("source", "web");
        append.Parameters.AddWithValue("action", "foundation.test");
        append.Parameters.AddWithValue("resource_type", "test");
        append.Parameters.AddWithValue("resource_id", Guid.NewGuid());
        append.Parameters.AddWithValue("outcome", "success");
        append.Parameters.AddWithValue("correlation_id", correlationId);
        append.Parameters.AddWithValue("reason", "redacted");
        append.Parameters.AddWithValue("metadata", NpgsqlTypes.NpgsqlDbType.Jsonb, "{}");
        await append.ExecuteNonQueryAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM audit.audit_events WHERE actor_user_id = @actor_user_id AND correlation_id = @correlation_id",
            administrator);
        count.Parameters.AddWithValue("actor_user_id", actorId);
        count.Parameters.AddWithValue("correlation_id", correlationId);

        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    private static async Task<Guid> SeedAuditEventAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var auditEvent = new AuditEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AuditSource.Web,
            "foundation.test",
            "test",
            Guid.NewGuid(),
            AuditOutcome.Success,
            Guid.NewGuid(),
            "redacted",
            "{}");

        administrator.AuditEvents.Add(auditEvent);
        await administrator.SaveChangesAsync();
        return auditEvent.Id;
    }
}
