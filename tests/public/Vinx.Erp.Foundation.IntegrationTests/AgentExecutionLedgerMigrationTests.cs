using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AgentExecutionLedgerMigrationTests
{
    [Fact]
    public async Task Agent_execution_ledger_is_company_scoped_minimal_and_least_privilege()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regclass('foundation.agent_execution_ledger') IS NOT NULL,
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.agent_execution_ledger'::regclass
                      AND conname = 'fk_agent_execution_ledger_companies_company_id'),
                EXISTS (
                    SELECT 1
                    FROM pg_indexes
                    WHERE schemaname = 'foundation'
                      AND tablename = 'agent_execution_ledger'
                      AND indexname = 'ix_agent_execution_ledger_company_id_actor_user_id_channel_request_id'
                      AND indexdef LIKE '%UNIQUE%'),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.agent_execution_ledger'::regclass
                      AND conname = 'ck_agent_execution_ledger_payload_hash_format'),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.agent_execution_ledger'::regclass
                      AND conname = 'ck_agent_execution_ledger_state'),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'foundation.agent_execution_ledger'::regclass
                      AND conname = 'ck_agent_execution_ledger_terminal_data'),
                EXISTS (
                    SELECT 1
                    FROM pg_class
                    WHERE oid = 'foundation.agent_execution_ledger'::regclass
                      AND relrowsecurity
                      AND relforcerowsecurity),
                EXISTS (
                    SELECT 1
                    FROM pg_policies
                    WHERE schemaname = 'foundation'
                      AND tablename = 'agent_execution_ledger'
                      AND policyname = 'agent_execution_ledger_company_isolation'),
                has_table_privilege('vinx_runtime', 'foundation.agent_execution_ledger', 'SELECT'),
                has_table_privilege('vinx_runtime', 'foundation.agent_execution_ledger', 'INSERT'),
                has_table_privilege('vinx_runtime', 'foundation.agent_execution_ledger', 'UPDATE'),
                has_table_privilege('vinx_runtime', 'foundation.agent_execution_ledger', 'DELETE'),
                has_column_privilege('vinx_runtime', 'foundation.agent_execution_ledger', 'state', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.agent_execution_ledger', 'safe_result_json', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.agent_execution_ledger', 'reason_code', 'UPDATE'),
                has_column_privilege('vinx_runtime', 'foundation.agent_execution_ledger', 'updated_at', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.agent_execution_ledger', 'company_id', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.agent_execution_ledger', 'actor_user_id', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.agent_execution_ledger', 'channel', 'UPDATE'),
                NOT has_column_privilege('vinx_runtime', 'foundation.agent_execution_ledger', 'request_id', 'UPDATE'),
                NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'foundation'
                      AND table_name = 'agent_execution_ledger'
                      AND column_name IN ('prompt', 'content', 'address'));
            """,
            administrator);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        for (var ordinal = 0; ordinal < 10; ordinal++)
        {
            Assert.True(reader.GetBoolean(ordinal));
        }

        Assert.False(reader.GetBoolean(10));
        Assert.False(reader.GetBoolean(11));
        for (var ordinal = 12; ordinal < 21; ordinal++)
        {
            Assert.True(reader.GetBoolean(ordinal));
        }
    }
}
