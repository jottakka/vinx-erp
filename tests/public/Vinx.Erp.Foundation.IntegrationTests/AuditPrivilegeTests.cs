using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AuditPrivilegeTests
{
    [Fact]
    public async Task Audit_append_only_trigger_rejects_a_table_writer_that_is_not_the_audit_owner()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var auditEventId = await SeedAuditEventAsync(fixture);

        await using var writer = await OpenTableWriterAsync(fixture);

        await using var update = new NpgsqlCommand(
            "UPDATE audit.audit_events SET action = 'changed' WHERE id = @id",
            writer
        );
        update.Parameters.AddWithValue("id", auditEventId);
        var updateException = await Assert.ThrowsAsync<PostgresException>(async () =>
            await update.ExecuteNonQueryAsync()
        );
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, updateException.SqlState);

        await using var delete = new NpgsqlCommand(
            "DELETE FROM audit.audit_events WHERE id = @id",
            writer
        );
        delete.Parameters.AddWithValue("id", auditEventId);
        var deleteException = await Assert.ThrowsAsync<PostgresException>(async () =>
            await delete.ExecuteNonQueryAsync()
        );
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleteException.SqlState);
    }

    [Fact]
    public async Task Audit_objects_are_owned_by_the_dedicated_owner_and_only_authorized_runtime_or_owner_roles_can_execute_the_appender()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid = 'audit.audit_events'::regclass)),
                pg_get_userbyid((SELECT proowner FROM pg_proc WHERE oid = 'audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)'::regprocedure)),
                has_table_privilege('vinx_runtime', 'audit.audit_events', 'SELECT'),
                has_table_privilege('vinx_runtime', 'audit.audit_events', 'INSERT'),
                has_table_privilege('vinx_runtime', 'audit.audit_events', 'UPDATE'),
                has_table_privilege('vinx_runtime', 'audit.audit_events', 'DELETE'),
                has_function_privilege('vinx_runtime', 'audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)', 'EXECUTE'),
                has_function_privilege('vinx_runtime', 'audit.reject_audit_event_mutation()', 'EXECUTE'),
                appender.prosecdef,
                appender.proconfig,
                has_function_privilege('vinx_platform_owner', 'audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)', 'EXECUTE'),
                has_function_privilege('vinx_provisioning_owner', 'audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)', 'EXECUTE'),
                has_function_privilege('vinx_employee_directory_owner', 'audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)', 'EXECUTE'),
                has_function_privilege('vinx_worker_owner', 'audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)', 'EXECUTE'),
                has_function_privilege('vinx_notifications_owner', 'audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)', 'EXECUTE'),
                -- O convite de membro audita o sucesso como vinx_permissions_owner
                -- (função SECURITY DEFINER que chama o appender).
                has_function_privilege('vinx_permissions_owner', 'audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)', 'EXECUTE'),
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
                      AND privilege.privilege_type = 'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(appender.proacl, acldefault('f', appender.proowner))) AS privilege
                    WHERE privilege.privilege_type = 'EXECUTE'
                      AND privilege.grantee NOT IN (
                          0,
                          appender.proowner,
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_runtime'),
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_platform_owner'),
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_provisioning_owner'),
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_employee_directory_owner'),
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_worker_owner'),
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_notifications_owner'),
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_permissions_owner'),
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_identity_owner')))
            FROM pg_proc AS appender
            WHERE appender.oid = 'audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)'::regprocedure
            """,
            administrator
        );
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
        Assert.Equal(["search_path=pg_catalog, audit, pg_temp"], reader.GetFieldValue<string[]>(9));
        Assert.True(reader.GetBoolean(10));
        Assert.True(reader.GetBoolean(11));
        Assert.True(reader.GetBoolean(12));
        Assert.True(reader.GetBoolean(13));
        Assert.True(reader.GetBoolean(14));
        Assert.True(reader.GetBoolean(15));
        Assert.True(reader.GetBoolean(16));
        Assert.True(reader.GetBoolean(17));
        Assert.True(reader.GetBoolean(18));
    }

    [Theory]
    [InlineData(AuditScope.Global, true)]
    [InlineData(AuditScope.Company, false)]
    public void Audit_scope_rejects_invalid_requested_company_pair(
        AuditScope scope,
        bool hasRequestedCompany
    )
    {
        Assert.Throws<ArgumentException>(() =>
            new AuditEvent(
                Guid.NewGuid(),
                Guid.NewGuid(),
                scope,
                hasRequestedCompany ? Guid.NewGuid() : null,
                AuditSource.Web,
                "another_action",
                "authorization",
                null,
                AuditOutcome.Denied,
                Guid.NewGuid(),
                "redacted",
                "{}"
            )
        );
    }

    [Fact]
    public void Global_audit_event_allows_no_requested_company()
    {
        var auditEvent = new AuditEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AuditScope.Global,
            null,
            AuditSource.Web,
            "another_action",
            "authorization",
            null,
            AuditOutcome.Success,
            Guid.NewGuid(),
            "redacted",
            "{}"
        );

        Assert.Null(auditEvent.RequestedCompanyId);
    }

    [Fact]
    public async Task Runtime_role_can_append_global_and_company_events_only_through_the_security_definer_function()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        var globalActorId = Guid.NewGuid();
        var globalCorrelationId = Guid.NewGuid();
        var companyActorId = Guid.NewGuid();
        var companyCorrelationId = Guid.NewGuid();
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await AppendAsync(runtime, globalActorId, "global", null, globalCorrelationId);
        await AppendAsync(runtime, companyActorId, "company", Guid.NewGuid(), companyCorrelationId);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT scope, requested_company_id
            FROM audit.audit_events
            WHERE (actor_user_id = @global_actor_user_id AND correlation_id = @global_correlation_id)
               OR (actor_user_id = @company_actor_user_id AND correlation_id = @company_correlation_id)
            ORDER BY scope
            """,
            administrator
        );
        command.Parameters.AddWithValue("global_actor_user_id", globalActorId);
        command.Parameters.AddWithValue("global_correlation_id", globalCorrelationId);
        command.Parameters.AddWithValue("company_actor_user_id", companyActorId);
        command.Parameters.AddWithValue("company_correlation_id", companyCorrelationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("company", reader.GetString(0));
        Assert.NotEqual(DBNull.Value, reader.GetValue(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("global", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.False(await reader.ReadAsync());
    }

    [Theory]
    [InlineData("global", true)]
    [InlineData("company", false)]
    [InlineData(null, false)]
    [InlineData("unknown", false)]
    public async Task Runtime_appender_rejects_invalid_scope_company_pairs_with_check_violation(
        string? scope,
        bool hasCompany
    )
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await AppendAsync(
                runtime,
                Guid.NewGuid(),
                scope,
                hasCompany ? Guid.NewGuid() : null,
                Guid.NewGuid()
            )
        );

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Audit_appender_replaces_the_old_signature_with_one_new_signature()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regprocedure('audit.append_event(uuid,uuid,text,text,text,uuid,text,uuid,text,jsonb)') IS NULL,
                to_regprocedure('audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)') IS NOT NULL,
                (SELECT count(*) FROM pg_proc
                 WHERE pronamespace = 'audit'::regnamespace AND proname = 'append_event')
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    private static async Task AppendAsync(
        NpgsqlConnection connection,
        Guid actorId,
        string? scope,
        Guid? requestedCompanyId,
        Guid correlationId
    )
    {
        await using var append = new NpgsqlCommand(
            """
            SELECT audit.append_event(
                @actor_user_id, @scope, @requested_company_id, @source, @action,
                @resource_type, @resource_id, @outcome, @correlation_id, @reason, @metadata)
            """,
            connection
        );
        append.Parameters.AddWithValue("actor_user_id", actorId);
        append.Parameters.AddWithValue("scope", (object?)scope ?? DBNull.Value);
        append.Parameters.AddWithValue(
            "requested_company_id",
            (object?)requestedCompanyId ?? DBNull.Value
        );
        append.Parameters.AddWithValue("source", "web");
        append.Parameters.AddWithValue("action", "foundation.test");
        append.Parameters.AddWithValue("resource_type", "test");
        append.Parameters.AddWithValue("resource_id", Guid.NewGuid());
        append.Parameters.AddWithValue("outcome", "success");
        append.Parameters.AddWithValue("correlation_id", correlationId);
        append.Parameters.AddWithValue("reason", "redacted");
        append.Parameters.AddWithValue("metadata", NpgsqlTypes.NpgsqlDbType.Jsonb, "{}");
        await append.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> SeedAuditEventAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var auditEvent = new AuditEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AuditScope.Company,
            Guid.NewGuid(),
            AuditSource.Web,
            "foundation.test",
            "test",
            Guid.NewGuid(),
            AuditOutcome.Success,
            Guid.NewGuid(),
            "redacted",
            "{}"
        );

        administrator.AuditEvents.Add(auditEvent);
        await administrator.SaveChangesAsync();
        return auditEvent.Id;
    }

    private static async Task<NpgsqlConnection> OpenTableWriterAsync(
        FoundationPostgresFixture fixture
    )
    {
        var roleName = $"audit_trigger_writer_{Guid.NewGuid():N}";
        var password = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (
            var grant = new NpgsqlCommand(
                $"""
                CREATE ROLE {roleName} LOGIN PASSWORD '{password}';
                GRANT USAGE ON SCHEMA audit TO {roleName};
                GRANT SELECT, UPDATE, DELETE ON TABLE audit.audit_events TO {roleName};
                """,
                administrator
            )
        )
        {
            await grant.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.RuntimeConnectionString)
        {
            Username = roleName,
            Password = password,
        }.ConnectionString;
        var writer = new NpgsqlConnection(connectionString);
        await writer.OpenAsync();
        return writer;
    }
}
