using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class ApprovalsMigrationTests
{
    [Fact]
    public async Task Runtime_can_update_only_approval_lifecycle_columns()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                has_table_privilege('vinx_runtime', 'foundation.approvals', 'SELECT'),
                has_table_privilege('vinx_runtime', 'foundation.approvals', 'INSERT'),
                has_table_privilege('vinx_runtime', 'foundation.approvals', 'UPDATE'),
                has_table_privilege('vinx_runtime', 'foundation.approvals', 'DELETE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'confirmation_request_id', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'consumed_at', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'result_json', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'updated_by_user_id', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'updated_at', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'request_id', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'row_version', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'deleted_at', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'deleted_by_user_id', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.approvals', 'deleted_reason', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'id', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'company_id', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'actor_user_id', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'action', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'payload_hash', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'policy_version', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'proposal_id', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'expires_at', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.approvals', 'created_at', 'UPDATE')
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        for (var ordinal = 4; ordinal < 23; ordinal++)
        {
            Assert.True(reader.GetBoolean(ordinal));
        }
    }

    [Fact]
    public async Task Approvals_declare_the_schema_invariants_as_database_constraints()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.approvals'::regclass
                      AND conname = 'ck_approvals_expires_after_creation'),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.approvals'::regclass
                      AND conname = 'ck_approvals_payload_hash_format'),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.approvals'::regclass
                      AND conname = 'ck_approvals_veto_complete'),
                EXISTS (
                    SELECT 1
                    FROM pg_indexes
                    WHERE schemaname = 'foundation'
                      AND tablename = 'approvals'
                      AND indexname = 'ix_approvals_proposal_id'
                      AND indexdef LIKE '%UNIQUE%'),
                EXISTS (
                    SELECT 1
                    FROM pg_indexes
                    WHERE schemaname = 'foundation'
                      AND tablename = 'approvals'
                      AND indexname = 'ix_approvals_confirmation_request_id'
                      AND indexdef LIKE '%UNIQUE%'
                      AND indexdef LIKE '%confirmation_request_id IS NOT NULL%')
            """, administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        for (var ordinal = 0; ordinal < 5; ordinal++)
        {
            Assert.True(reader.GetBoolean(ordinal));
        }
    }

    [Fact]
    public async Task Ai_writes_confirm_capability_is_seeded_with_its_stable_identity()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id FROM foundation.capabilities WHERE name = @name", administrator);
        command.Parameters.AddWithValue("name", KnownCapabilities.AiWritesConfirm);

        Assert.Equal(KnownCapabilities.AiWritesConfirmId, await command.ExecuteScalarAsync());
    }
}
