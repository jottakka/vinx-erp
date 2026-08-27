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

    [Fact]
    public async Task Stateful_downgrade_refuses_to_erase_the_execution_ledger()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(FinalBranchMigration);

        var seed = await SeedCompanyWithFileAsync(fixture);
        await ExecuteAsync(
            fixture,
            """
            INSERT INTO foundation.agent_execution_ledger (
                id, company_id, actor_user_id, channel, request_id, journey,
                payload_hash, state, safe_result_json, created_at, updated_at)
            VALUES (
                @ledger_id, @company_id, @actor, 'web', @request_id, 'summarize',
                @payload_hash, 'completed', '{}'::jsonb, clock_timestamp(), clock_timestamp())
            """,
            ("ledger_id", Guid.NewGuid()),
            ("company_id", seed.CompanyId),
            ("actor", seed.UserId),
            ("request_id", Guid.NewGuid()),
            ("payload_hash", new string('a', 64)));

        var refusal = await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.Database.MigrateAsync(MainDraftCreatedWorkerGrant));
        Assert.Equal("23514", refusal.SqlState);

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM foundation.agent_execution_ledger WHERE company_id = @company_id",
                administrator);
            command.Parameters.AddWithValue("company_id", seed.CompanyId);
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task Stateful_downgrade_refuses_to_erase_proposals_and_draft_provenance()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(FinalBranchMigration);

        var seed = await SeedCompanyWithFileAsync(fixture);
        await ExecuteAsync(
            fixture,
            """
            INSERT INTO paguai.draft_proposals (
                id, company_id, actor_user_id, request_id, file_record_id,
                title, description, amount, currency, due_date,
                beneficiary_display_name, proof_requirement, confidence,
                source_references, policy_version, payload_hash, state,
                created_at, expires_at)
            VALUES (
                @proposal_id, @company_id, @actor, @request_id, @file_id,
                'Título', 'Descrição', 42.00, 'BRL', current_date + 30,
                'Fornecedor', 'required', 0.9,
                '[]'::jsonb, 'test-policy-v1', @payload_hash, 'pending',
                clock_timestamp(), clock_timestamp() + interval '15 minutes')
            """,
            ("proposal_id", seed.ProposalId),
            ("company_id", seed.CompanyId),
            ("actor", seed.UserId),
            ("request_id", Guid.NewGuid()),
            ("file_id", seed.FileId),
            ("payload_hash", new string('b', 64)));
        await ExecuteAsync(
            fixture,
            """
            INSERT INTO paguai.requests (
                id, company_id, requester_user_id, request_id, title, description,
                amount, currency, due_date, beneficiary_display_name,
                proof_requirement, source_proposal_id, state, version, created_at)
            VALUES (
                @request_row_id, @company_id, @actor, @request_id, 'Rascunho',
                'Preserva a proveniência', 42.00, 'BRL', current_date + 30,
                'Fornecedor', 'required', @proposal_id, 'rascunho', 1, clock_timestamp())
            """,
            ("request_row_id", Guid.NewGuid()),
            ("company_id", seed.CompanyId),
            ("actor", seed.UserId),
            ("request_id", Guid.NewGuid()),
            ("proposal_id", seed.ProposalId));

        var refusal = await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.Database.MigrateAsync(MainDraftCreatedWorkerGrant));
        Assert.Equal("23514", refusal.SqlState);

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM paguai.requests WHERE source_proposal_id = @proposal_id",
                administrator);
            command.Parameters.AddWithValue("proposal_id", seed.ProposalId);
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task Downgraded_telegram_functions_keep_the_pre_branch_contract()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(FinalBranchMigration);
        await migrator.Database.MigrateAsync(MainDraftCreatedWorkerGrant);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();

        await using (var nullTarget = administrator.CreateCommand())
        {
            nullTarget.CommandText =
                "SELECT notifications.get_telegram_notification_delivery_target(NULL, NULL)";
            var invalid = await Assert.ThrowsAsync<PostgresException>(
                () => nullTarget.ExecuteScalarAsync());
            Assert.Equal("22023", invalid.SqlState);
        }

        await using (var nullReceipt = administrator.CreateCommand())
        {
            nullReceipt.CommandText = "SELECT notifications.complete_inbound_telegram_text(NULL)";
            var invalid = await Assert.ThrowsAsync<PostgresException>(
                () => nullReceipt.ExecuteScalarAsync());
            Assert.Equal("22023", invalid.SqlState);
        }

        await using (var emptyTarget = administrator.CreateCommand())
        {
            emptyTarget.CommandText =
                "SELECT * FROM notifications.get_telegram_notification_delivery_target(@delivery, @attempt)";
            emptyTarget.Parameters.AddWithValue("delivery", Guid.NewGuid());
            emptyTarget.Parameters.AddWithValue("attempt", Guid.NewGuid());
            await using var reader = await emptyTarget.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.False(await reader.ReadAsync());
        }
    }

    private sealed record CompositionSeed(
        Guid UserId,
        Guid CompanyId,
        Guid FileId,
        Guid ProposalId);

    private static async Task<CompositionSeed> SeedCompanyWithFileAsync(
        FoundationPostgresFixture fixture)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var actor = new Vinx.Erp.Foundation.Domain.User(
            Guid.NewGuid(), "Ator de composição", "composition@example.test");
        var requestId = Guid.NewGuid();
        var group = new Vinx.Erp.Foundation.Domain.Group(
            Guid.NewGuid(), "Grupo de composição", actor.Id, requestId);
        var company = new Vinx.Erp.Foundation.Domain.Company(
            Guid.NewGuid(), group.Id, "Empresa de composição", actor.Id, requestId);
        var membership = new Vinx.Erp.Foundation.Domain.Membership(
            Guid.NewGuid(), company.Id, actor.Id, actor.Id, requestId);
        administrator.AddRange(actor, group, company, membership);
        await administrator.SaveChangesAsync();

        var fileId = Guid.NewGuid();
        await ExecuteAsync(
            fixture,
            """
            INSERT INTO foundation.files (
                id, company_id, uploader_membership_id, original_file_name,
                extension, size_bytes, mime_type, checksum, classification,
                origin, retention_until, provider_key, processing_state,
                scan_engine, scan_engine_version, scan_signature_version,
                scanned_at_utc, created_by_user_id, updated_by_user_id, request_id,
                created_at, updated_at)
            VALUES (
                @file_id, @company_id, @membership_id, 'composition.pdf', '.pdf', 1,
                'application/pdf', @checksum, 'agentic_attachment', 'agent_channel',
                clock_timestamp() + interval '30 days', @provider_key, 'confirmed',
                'TestScanner', '1', 'test-signature', clock_timestamp(),
                @actor, @actor, @request_id, clock_timestamp(), clock_timestamp())
            """,
            ("file_id", fileId),
            ("company_id", company.Id),
            ("membership_id", membership.Id),
            ("checksum", "sha256:" + new string('c', 64)),
            ("provider_key", $"quarantine/{company.Id:N}/{fileId:N}"),
            ("actor", actor.Id),
            ("request_id", requestId));

        return new CompositionSeed(actor.Id, company.Id, fileId, Guid.NewGuid());
    }

    private static async Task ExecuteAsync(
        FoundationPostgresFixture fixture,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
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
