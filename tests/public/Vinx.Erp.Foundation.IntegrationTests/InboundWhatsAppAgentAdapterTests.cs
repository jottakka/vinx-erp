using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vinx.Erp.AI;
using Vinx.Erp.ApiHost.Agent;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Casos PostgreSQL planejados para a fronteira opaca do adapter. A execução
/// local fica adiada nesta entrega por decisão explícita (sem Docker/Testcontainers).
/// </summary>
[Collection("notification-worker")]
public sealed class InboundWhatsAppAgentAdapterTests
{
    [Fact]
    public async Task Admitted_document_reaches_the_shared_core_with_only_a_confirmed_file_id()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        var membershipId = await GrantAgentDocumentCapabilitiesAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId);
        var inboundId = Guid.NewGuid();
        await AdmitDocumentAsync(factory, inboundId, seed, membershipId);

        await using (var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await using var claim = new NpgsqlCommand(
                "SELECT status, claimed, message_type FROM notifications.claim_inbound_whatsapp_agent(@id)",
                worker);
            claim.Parameters.AddWithValue("id", inboundId);
            await using var reader = await claim.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("agent_in_flight", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal("document", reader.GetString(2));
        }

        var fileId = await SeedConfirmedAgentFileAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            membershipId,
            inboundId);
        await using (var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await using var record = new NpgsqlCommand(
                "SELECT notifications.record_inbound_whatsapp_agent_file(@id, @file_id)",
                worker);
            record.Parameters.AddWithValue("id", inboundId);
            record.Parameters.AddWithValue("file_id", fileId);
            Assert.True((bool)(await record.ExecuteScalarAsync())!);
        }

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var read = new NpgsqlCommand(
            "SELECT agent_file_id, agent_status FROM notifications.inbound_whatsapp_messages WHERE id = @id",
            administrator);
        read.Parameters.AddWithValue("id", inboundId);
        await using var state = await read.ExecuteReaderAsync();
        Assert.True(await state.ReadAsync());
        Assert.Equal(fileId, state.GetGuid(0));
        Assert.Equal("agent_in_flight", state.GetString(1));
    }

    [Fact]
    public async Task Revoked_capability_prevents_the_core_claim_and_creates_no_execution()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        var membershipId = await GrantAgentDocumentCapabilitiesAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId);
        var inboundId = Guid.NewGuid();
        await AdmitDocumentAsync(factory, inboundId, seed, membershipId);

        await using (var administrator = await factory.Fixture.OpenAdministratorConnectionAsync())
        {
            await using var revoke = new NpgsqlCommand(
                "UPDATE foundation.role_assignments SET deleted_at = clock_timestamp(), deleted_by_user_id = @actor WHERE company_id = @company_id AND membership_id = @membership_id",
                administrator);
            revoke.Parameters.AddWithValue("actor", factory.ActorUserId);
            revoke.Parameters.AddWithValue("company_id", seed.CompanyId);
            revoke.Parameters.AddWithValue("membership_id", membershipId);
            await revoke.ExecuteNonQueryAsync();
        }

        await using (var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await using var claim = new NpgsqlCommand(
                "SELECT status, claimed FROM notifications.claim_inbound_whatsapp_agent(@id)",
                worker);
            claim.Parameters.AddWithValue("id", inboundId);
            await using var reader = await claim.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("capability_missing", reader.GetString(0));
            Assert.False(reader.GetBoolean(1));
        }

        await using var admin = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var executions = new NpgsqlCommand(
            "SELECT count(*) FROM foundation.agent_execution_ledger WHERE inbound_message_id = @id",
            admin);
        executions.Parameters.AddWithValue("id", inboundId);
        Assert.Equal(0L, (long)(await executions.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Document_without_draft_create_never_claims_channel_agent()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        var membershipId = await GrantAgentDocumentCapabilitiesAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            includePaguaiDraftCreate: false);
        var inboundId = Guid.NewGuid();
        await AdmitDocumentAsync(factory, inboundId, seed, membershipId);

        await using var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var claim = new NpgsqlCommand(
            "SELECT status, claimed FROM notifications.claim_inbound_whatsapp_agent(@id)",
            worker);
        claim.Parameters.AddWithValue("id", inboundId);
        await using var reader = await claim.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("capability_missing", reader.GetString(0));
        Assert.False(reader.GetBoolean(1));
    }

    [Fact]
    public async Task Agent_claim_read_file_and_finalize_functions_are_worker_only()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT proname,
                   prosecdef,
                   proconfig,
                   has_function_privilege('vinx_worker', oid, 'EXECUTE'),
                   NOT has_function_privilege('vinx_runtime', oid, 'EXECUTE'),
                   NOT has_function_privilege('public', oid, 'EXECUTE')
            FROM pg_proc
            WHERE oid IN (
                'notifications.claim_inbound_whatsapp_agent(uuid)'::regprocedure,
                'notifications.record_inbound_whatsapp_agent_file(uuid,uuid)'::regprocedure,
                'notifications.release_inbound_whatsapp_agent(uuid)'::regprocedure,
                'notifications.finalize_inbound_whatsapp_agent(uuid,text,text)'::regprocedure)
            ORDER BY proname
            """,
            administrator);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(
                ["search_path=pg_catalog, notifications, pg_temp"],
                reader.GetFieldValue<string[]>(2));
            Assert.True(reader.GetBoolean(3));
            Assert.True(reader.GetBoolean(4));
            Assert.True(reader.GetBoolean(5));
        }

        Assert.Equal(
            [
                "claim_inbound_whatsapp_agent",
                "finalize_inbound_whatsapp_agent",
                "record_inbound_whatsapp_agent_file",
                "release_inbound_whatsapp_agent",
            ],
            names);
    }

    [Fact]
    public async Task Completed_ledger_redelivery_resumes_one_reply_without_model_call()
    {
        var model = new NeverCalledAgentModel();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            agentModel: model,
            agentRunOptions: ConfiguredAgentOptions());
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        var membershipId = await GrantAgentDocumentCapabilitiesAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId);
        var inboundId = Guid.NewGuid();
        await AdmitImageAsync(factory, inboundId, seed, membershipId);

        await using (var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await using var claim = new NpgsqlCommand(
                "SELECT claimed FROM notifications.claim_inbound_whatsapp_agent(@id)",
                worker);
            claim.Parameters.AddWithValue("id", inboundId);
            Assert.True((bool)(await claim.ExecuteScalarAsync())!);
        }

        var fileId = await SeedConfirmedAgentFileAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            membershipId,
            inboundId);
        await using (var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await using var record = new NpgsqlCommand(
                "SELECT notifications.record_inbound_whatsapp_agent_file(@id, @file_id)",
                worker);
            record.Parameters.AddWithValue("id", inboundId);
            record.Parameters.AddWithValue("file_id", fileId);
            Assert.True((bool)(await record.ExecuteScalarAsync())!);
        }

        var actor = new ActorContext(seed.RecipientUserId, Guid.NewGuid());
        var command = new AgentCommand(
            actor,
            "whatsapp",
            inboundId,
            inboundId,
            AgentTaskProfileCatalog.ReadOnlySummarizeProfileId,
            "Analise o anexo sintético.",
            seed.CompanyId,
            [fileId]);
        await SeedCompletedLedgerAsync(factory, command, "Resposta segura");

        await using (var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await using var recovery = new NpgsqlCommand(
                "SELECT status, claimed, agent_file_id FROM notifications.claim_inbound_whatsapp_agent(@id)",
                worker);
            recovery.Parameters.AddWithValue("id", inboundId);
            await using var recovered = await recovery.ExecuteReaderAsync();
            Assert.True(await recovered.ReadAsync());
            Assert.Equal("terminal_recovery", recovered.GetString(0));
            Assert.True(recovered.GetBoolean(1));
            Assert.Equal(fileId, recovered.GetGuid(2));
        }

        AgentExecutionResult execution;
        using (var scope = factory.Services.CreateScope())
        {
            execution = await scope.ServiceProvider
                .GetRequiredService<AgentExecutionService>()
                .ExecuteAsync(command, CancellationToken.None);
        }
        Assert.Equal(AgentExecutionDisposition.Completed, execution.Disposition);
        Assert.Equal("Resposta segura", execution.Outcome?.Text);
        Assert.Equal(0, model.CallCount);

        await using (var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await using var begin = new NpgsqlCommand(
                "SELECT notifications.begin_inbound_whatsapp_reply(@id)",
                worker);
            begin.Parameters.AddWithValue("id", inboundId);
            Assert.True((bool)(await begin.ExecuteScalarAsync())!);
            Assert.False((bool)(await begin.ExecuteScalarAsync())!);

            await using var complete = new NpgsqlCommand(
                "SELECT notifications.complete_inbound_whatsapp_message(@id, 'wamid.synthetic.reply')",
                worker);
            complete.Parameters.AddWithValue("id", inboundId);
            Assert.True((bool)(await complete.ExecuteScalarAsync())!);

            await using var finalize = new NpgsqlCommand(
                "SELECT notifications.finalize_inbound_whatsapp_agent(@id, 'completed', NULL)",
                worker);
            finalize.Parameters.AddWithValue("id", inboundId);
            Assert.True((bool)(await finalize.ExecuteScalarAsync())!);
        }
    }

    internal static async Task<Guid> GrantAgentDocumentCapabilitiesAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid userId,
        bool includePaguaiDraftCreate = true)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var membership = new NpgsqlCommand(
            "SELECT id FROM foundation.memberships WHERE company_id = @company_id AND user_id = @user_id AND deleted_at IS NULL",
            administrator);
        membership.Parameters.AddWithValue("company_id", companyId);
        membership.Parameters.AddWithValue("user_id", userId);
        var membershipId = (Guid)(await membership.ExecuteScalarAsync())!;
        var roleId = Guid.NewGuid();
        await using var grant = new NpgsqlCommand(
            """
            INSERT INTO foundation.roles (
                id, company_id, name, created_by_user_id, updated_by_user_id,
                request_id, created_at, updated_at)
            VALUES (
                @role_id, @company_id, 'Agent channel', @actor, @actor,
                @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.role_assignments (
                id, company_id, role_id, membership_id, created_by_user_id,
                updated_by_user_id, request_id, created_at, updated_at)
            VALUES (
                gen_random_uuid(), @company_id, @role_id, @membership_id, @actor,
                @actor, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.role_capabilities (
                id, company_id, role_id, capability_id, scope, created_by_user_id,
                updated_by_user_id, request_id, created_at, updated_at)
            SELECT gen_random_uuid(), @company_id, @role_id, capability_id, 'company',
                   @actor, @actor, @request_id, clock_timestamp(), clock_timestamp()
            FROM unnest(@capability_ids::uuid[]) AS capability_id;
            """,
            administrator);
        grant.Parameters.AddWithValue("role_id", roleId);
        grant.Parameters.AddWithValue("company_id", companyId);
        grant.Parameters.AddWithValue("membership_id", membershipId);
        grant.Parameters.AddWithValue("actor", factory.ActorUserId);
        grant.Parameters.AddWithValue("request_id", Guid.NewGuid());
        var capabilityIds = new List<Guid>
        {
            KnownCapabilities.AiReadId,
            KnownCapabilities.FilesUploadId,
            KnownCapabilities.FilesReadOcrId,
        };
        if (includePaguaiDraftCreate)
        {
            capabilityIds.Add(KnownCapabilities.PaguaiDraftCreateId);
        }

        grant.Parameters.AddWithValue("capability_ids", capabilityIds.ToArray());
        await grant.ExecuteNonQueryAsync();
        return membershipId;
    }

    private static async Task AdmitDocumentAsync(
        FoundationApiFactory factory,
        Guid inboundId,
        NotificationCompanySeed seed,
        Guid membershipId)
    {
        await using var runtime = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT notifications.admit_inbound_whatsapp_media(
                @id, '111111111111111', @message_id, '556198132901',
                'document', @media_id, NULL, 'admitted', @user_id,
                @company_id, @membership_id)
            """,
            runtime);
        command.Parameters.AddWithValue("id", inboundId);
        command.Parameters.AddWithValue("message_id", $"wamid.agent.{inboundId:N}");
        command.Parameters.AddWithValue("media_id", $"media-{inboundId:N}");
        command.Parameters.AddWithValue("user_id", seed.RecipientUserId);
        command.Parameters.AddWithValue("company_id", seed.CompanyId);
        command.Parameters.AddWithValue("membership_id", membershipId);
        Assert.Equal("created", (string)(await command.ExecuteScalarAsync())!);
    }

    private static async Task AdmitImageAsync(
        FoundationApiFactory factory,
        Guid inboundId,
        NotificationCompanySeed seed,
        Guid membershipId)
    {
        await using var runtime = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT notifications.admit_inbound_whatsapp_media(
                @id, '111111111111111', @message_id, '556198132901',
                'image', @media_id, 'Analise o anexo sintético.', 'admitted', @user_id,
                @company_id, @membership_id)
            """,
            runtime);
        command.Parameters.AddWithValue("id", inboundId);
        command.Parameters.AddWithValue("message_id", $"wamid.agent.{inboundId:N}");
        command.Parameters.AddWithValue("media_id", $"media-{inboundId:N}");
        command.Parameters.AddWithValue("user_id", seed.RecipientUserId);
        command.Parameters.AddWithValue("company_id", seed.CompanyId);
        command.Parameters.AddWithValue("membership_id", membershipId);
        Assert.Equal("created", (string)(await command.ExecuteScalarAsync())!);
    }

    internal static async Task<Guid> SeedConfirmedAgentFileAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid userId,
        Guid membershipId,
        Guid requestId)
    {
        var fileId = Guid.NewGuid();
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.files (
                id, company_id, uploader_membership_id, original_file_name,
                extension, size_bytes, mime_type, checksum, classification,
                origin, retention_until, provider_key, processing_state,
                scan_engine, scan_engine_version, scan_signature_version,
                scanned_at, created_by_user_id, updated_by_user_id, request_id,
                created_at, updated_at)
            VALUES (
                @id, @company_id, @membership_id, 'agent.pdf', '.pdf', 1,
                'application/pdf', @checksum, 'agentic_attachment', 'agent_channel',
                clock_timestamp() + interval '30 days', @provider_key, 'confirmed',
                'TestScanner', '1', 'test-signature', clock_timestamp(),
                @user_id, @user_id, @request_id, clock_timestamp(), clock_timestamp())
            """,
            administrator);
        command.Parameters.AddWithValue("id", fileId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("membership_id", membershipId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("request_id", requestId);
        command.Parameters.AddWithValue(
            "checksum",
            "sha256:ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb");
        command.Parameters.AddWithValue("provider_key", $"quarantine/{companyId:N}/{fileId:N}");
        await command.ExecuteNonQueryAsync();
        return fileId;
    }

    internal static async Task SeedCompletedLedgerAsync(
        FoundationApiFactory factory,
        AgentCommand command,
        string replyText)
    {
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new
            {
                command.Journey,
                command.Message,
                command.CompanyId,
                command.Attachments,
            })))).ToLowerInvariant();
        var ledger = AgentExecutionLedger.Create(
            Guid.NewGuid(),
            command.CompanyId!.Value,
            command.Actor.UserId,
            command.Channel,
            command.InboundMessageId,
            command.RequestId,
            command.Journey,
            payloadHash);
        ledger.MarkModelInFlight(command.Actor.UserId, command.RequestId);
        ledger.Complete(
            JsonSerializer.Serialize(new AgentOutcome(
                AgentOutcomeStatus.Completed,
                "ok",
                replyText,
                null,
                command.Actor.CorrelationId,
                TimeSpan.FromMilliseconds(1))),
            command.Actor.UserId,
            command.RequestId);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        administrator.AgentExecutionLedger.Add(ledger);
        await administrator.SaveChangesAsync();
    }

    internal static AgentRunOptions ConfiguredAgentOptions() => new()
    {
        Provider = AgentRunOptions.OpenAiCompatibleProvider,
        Endpoint = new Uri("https://agent-provider.example/v1/"),
        Model = "test-agent-model",
        ApprovedHosts = ["agent-provider.example"],
    };

    internal sealed class NeverCalledAgentModel : IAgentModel
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public Task<ChatResponse> GetResponseAsync(
            IReadOnlyList<ChatMessage> messages,
            AgentModelSelection selection,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            throw new InvalidOperationException("A terminal ledger replay must not call the model.");
        }
    }
}
