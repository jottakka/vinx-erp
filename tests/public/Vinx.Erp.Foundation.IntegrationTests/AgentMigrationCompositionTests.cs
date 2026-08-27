using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AgentMigrationCompositionTests
{
    private const string MainDraftCreatedWorkerGrant =
        "20260827120000_AddPaguaiDraftCreatedWorkerGrant";
    private const string FinalBranchMigration =
        "20260827121400_AddTelegramAgentAdapter";

    private static readonly string[] BranchMigrationIds =
    [
        "20260827121000_AddAgentExecutionLedger",
        "20260827121100_AddAgentExecutionLedgerConcurrencyAndChannelIntegrity",
        "20260827121200_AddPaguaiDraftProposals",
        "20260827121300_AddWhatsAppAgentAdapter",
        FinalBranchMigration,
    ];

    [Fact]
    public async Task Branch_applies_after_356_and_downgrades_to_356_without_removing_main_contracts()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();

        await migrator.Database.MigrateAsync(MainDraftCreatedWorkerGrant);
        await AssertMainContractsAsync(fixture);
        Assert.Equal(0, await CountBranchHistoryAsync(fixture));

        await migrator.Database.MigrateAsync(FinalBranchMigration);
        Assert.Equal(BranchMigrationIds.Length, await CountBranchHistoryAsync(fixture));
        await AssertBranchContractsPresentAsync(fixture);

        await migrator.Database.MigrateAsync(MainDraftCreatedWorkerGrant);

        Assert.Equal(0, await CountBranchHistoryAsync(fixture));
        await AssertBranchContractsRemovedAsync(fixture);
        await AssertMainContractsAsync(fixture);
    }

    private static async Task<int> CountBranchHistoryAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE migration_id = ANY(@ids)",
            administrator);
        command.Parameters.AddWithValue("ids", BranchMigrationIds);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task AssertBranchContractsPresentAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regclass('foundation.agent_execution_ledger') IS NOT NULL,
                to_regclass('paguai.draft_proposals') IS NOT NULL,
                to_regprocedure('notifications.claim_inbound_whatsapp_agent(uuid)') IS NOT NULL,
                to_regprocedure('notifications.claim_inbound_telegram_agent(uuid)') IS NOT NULL
            """,
            administrator);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < reader.FieldCount; index++)
        {
            Assert.True(reader.GetBoolean(index));
        }
    }

    private static async Task AssertBranchContractsRemovedAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regclass('foundation.agent_execution_ledger') IS NULL,
                to_regclass('paguai.draft_proposals') IS NULL,
                to_regprocedure('notifications.claim_inbound_whatsapp_agent(uuid)') IS NULL,
                to_regprocedure('notifications.claim_inbound_telegram_agent(uuid)') IS NULL,
                NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'notifications'
                        AND table_name = 'inbound_whatsapp_messages'
                        AND column_name = 'agent_status'),
                NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'notifications'
                        AND table_name = 'inbound_telegram_messages'
                        AND column_name = 'agent_status')
            """,
            administrator);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < reader.FieldCount; index++)
        {
            Assert.True(reader.GetBoolean(index));
        }
    }

    private static async Task AssertMainContractsAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1 FROM "__EFMigrationsHistory"
                    WHERE migration_id = '20260827120000_AddPaguaiDraftCreatedWorkerGrant'),
                to_regprocedure(
                    'notifications.admit_inbound_telegram_text(uuid,bigint,bigint,bigint,text)')
                    IS NOT NULL,
                to_regprocedure(
                    'notifications.get_notification_email_delivery_target(uuid,uuid)')
                    IS NOT NULL,
                to_regprocedure('paguai.list_draft_approver_user_ids(uuid,uuid)')
                    IS NOT NULL,
                has_schema_privilege('vinx_worker', 'paguai', 'USAGE'),
                has_function_privilege(
                    'vinx_worker',
                    'paguai.list_draft_approver_user_ids(uuid,uuid)',
                    'EXECUTE'),
                has_function_privilege(
                    'vinx_worker',
                    'notifications.get_notification_email_delivery_target(uuid,uuid)',
                    'EXECUTE'),
                to_regprocedure(
                    'notifications.enqueue_paguai_draft_created_notification(uuid,uuid,uuid,uuid)')
                    IS NOT NULL,
                has_function_privilege(
                    'vinx_worker',
                    'notifications.enqueue_paguai_draft_created_notification(uuid,uuid,uuid,uuid)',
                    'EXECUTE'),
                has_schema_privilege('vinx_notifications_owner', 'paguai', 'USAGE'),
                has_column_privilege(
                    'vinx_notifications_owner', 'paguai.requests', 'id', 'SELECT'),
                has_column_privilege(
                    'vinx_notifications_owner', 'paguai.requests', 'company_id', 'SELECT'),
                has_column_privilege(
                    'vinx_notifications_owner', 'paguai.requests', 'requester_user_id', 'SELECT'),
                EXISTS (
                    SELECT 1 FROM pg_policies
                    WHERE schemaname = 'paguai'
                        AND tablename = 'requests'
                        AND policyname = 'paguai_requests_notifications_owner_read'),
                EXISTS (
                    SELECT 1 FROM pg_policies
                    WHERE schemaname = 'foundation'
                        AND tablename = 'capabilities'
                        AND policyname = 'capabilities_notifications_owner_paguai_approve_read')
            """,
            administrator);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < reader.FieldCount; index++)
        {
            Assert.True(reader.GetBoolean(index));
        }
    }
}
