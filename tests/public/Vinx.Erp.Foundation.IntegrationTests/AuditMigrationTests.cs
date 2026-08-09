using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AuditMigrationTests
{
    private const string BeforeAuditScope = "20260809024707_RenameExternalIdentityProviderToIssuer";
    private const string AddAuditScope = "20260809184931_AddAuditScope";

    [Fact]
    public async Task Add_audit_scope_backfills_legacy_events_and_removes_the_legacy_appender()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeAuditScope);
        var companyId = Guid.NewGuid();

        await using (var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString))
        {
            await runtime.OpenAsync();
            await InsertLegacyEventAsync(runtime, companyId, "foundation.legacy.company");
            await InsertLegacyEventAsync(runtime, null, "company_context_required");
        }

        await using (var legacyAdministrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var legacyRows = new NpgsqlCommand(
            "SELECT count(*) FILTER (WHERE requested_company_id IS NULL), count(*) FILTER (WHERE requested_company_id IS NOT NULL) FROM audit.audit_events",
            legacyAdministrator))
        await using (var legacyReader = await legacyRows.ExecuteReaderAsync())
        {
            Assert.True(await legacyReader.ReadAsync());
            Assert.Equal(1L, legacyReader.GetInt64(0));
            Assert.Equal(1L, legacyReader.GetInt64(1));
        }

        await migrator.Database.MigrateAsync(AddAuditScope);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT action, scope, requested_company_id
            FROM audit.audit_events
            ORDER BY action
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("company_context_required", reader.GetString(0));
        Assert.Equal("global", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("foundation.legacy.company", reader.GetString(0));
        Assert.Equal("company", reader.GetString(1));
        Assert.False(reader.IsDBNull(2));

        await reader.CloseAsync();
        await using var grants = new NpgsqlCommand(
            "SELECT has_schema_privilege('vinx_audit_owner', 'audit', 'CREATE')",
            administrator);
        Assert.False((bool)(await grants.ExecuteScalarAsync() ?? true));
    }

    [Fact]
    public async Task Downgrade_restores_the_legacy_appender_when_every_event_is_company_scoped()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using (var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString))
        {
            await runtime.OpenAsync();
            await AppendCurrentEventAsync(runtime, "company", Guid.NewGuid());
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeAuditScope);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regprocedure('audit.append_event(uuid,uuid,text,text,text,uuid,text,uuid,text,jsonb)') IS NOT NULL,
                to_regprocedure('audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)') IS NULL,
                NOT EXISTS (
                    SELECT 1 FROM pg_attribute
                    WHERE attrelid = 'audit.audit_events'::regclass AND attname = 'scope')
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public async Task Downgrade_rejects_global_events_before_rewriting_or_deleting_them()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var correlationId = Guid.NewGuid();
        await using (var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString))
        {
            await runtime.OpenAsync();
            await AppendCurrentEventAsync(runtime, "global", null, correlationId);
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await migrator.Database.MigrateAsync(BeforeAuditScope));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT scope, requested_company_id,
                   to_regprocedure('audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)') IS NOT NULL,
                   has_schema_privilege('vinx_audit_owner', 'audit', 'CREATE')
            FROM audit.audit_events
            WHERE correlation_id = @correlation_id
            """, administrator);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("global", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
    }

    private static async Task InsertLegacyEventAsync(NpgsqlConnection runtime, Guid? companyId, string action)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT audit.append_event(
                @actor_user_id, @requested_company_id, 'web', @action, 'test', NULL,
                'denied', @correlation_id, 'redacted', '{}'::jsonb)
            """, runtime);
        command.Parameters.AddWithValue("actor_user_id", Guid.NewGuid());
        command.Parameters.AddWithValue("requested_company_id", (object?)companyId ?? DBNull.Value);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AppendCurrentEventAsync(
        NpgsqlConnection runtime,
        string scope,
        Guid? companyId,
        Guid? correlationId = null)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT audit.append_event(
                @actor_user_id, @scope, @requested_company_id, 'web', 'foundation.test', 'test', NULL,
                'success', @correlation_id, 'redacted', '{}'::jsonb)
            """, runtime);
        command.Parameters.AddWithValue("actor_user_id", Guid.NewGuid());
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("requested_company_id", (object?)companyId ?? DBNull.Value);
        command.Parameters.AddWithValue("correlation_id", correlationId ?? Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }
}
