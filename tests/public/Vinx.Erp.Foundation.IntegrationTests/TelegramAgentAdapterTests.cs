using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vinx.Erp.AI;
using Vinx.Erp.ApiHost.Notifications;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Casos PostgreSQL planejados para Task 8. A compilação integra o contrato;
/// a execução local fica adiada por decisão explícita (sem Testcontainers).
/// </summary>
[Collection("notification-worker")]
public sealed class TelegramAgentAdapterTests
{
    [Fact]
    public async Task Selection_endpoint_requires_authentication_and_the_exact_opaque_request()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<FoundationDbContext>(_ =>
            throw new InvalidOperationException(
                "Authorization must short-circuit before resolving the database."));
        await using var application = builder.Build();
        application.MapAgentChannelCompanySelectionEndpoints();
        await application.StartAsync();

        var route = Assert.Single(
            application.Services.GetRequiredService<EndpointDataSource>().Endpoints,
            endpoint => endpoint is RouteEndpoint candidate
                && candidate.RoutePattern.RawText == "/agent/channel-company-selection");
        Assert.NotEmpty(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(
            ["InboundMessageId", "CompanyId", "RequestId"],
            typeof(SelectAgentChannelCompanyRequest)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
    }

    [Fact]
    public async Task Selection_required_never_calls_the_model_and_accepts_only_the_logged_in_actor()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        const string address = "830001";
        var first = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: address,
            telegramContactValidated: true);
        var second = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: address,
            telegramContactValidated: true);
        await RebindCompanyToUserAsync(
            factory,
            second.CompanyId,
            second.RecipientUserId,
            first.RecipientUserId,
            address);
        var inboundId = Guid.NewGuid();

        var admission = await AdmitAsync(factory, inboundId, 1, 1, address);

        Assert.Equal("created", admission.Status);
        Assert.Equal("selection_required", admission.Resolution);
        Assert.Null(admission.DeliveryId);
        await using (var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await using (var receipt = new NpgsqlCommand(
                "SELECT status, transitioned FROM notifications.complete_inbound_telegram_text(@id)",
                worker))
            {
                receipt.Parameters.AddWithValue("id", inboundId);
                await using var completed = await receipt.ExecuteReaderAsync();
                Assert.True(await completed.ReadAsync());
                Assert.Equal("completed", completed.GetString(0));
                Assert.True(completed.GetBoolean(1));
            }
            await using (var selectionReply = new NpgsqlCommand(
                "SELECT delivery_id, created FROM notifications.enqueue_inbound_telegram_selection_reply(@id)",
                worker))
            {
                selectionReply.Parameters.AddWithValue("id", inboundId);
                await using var reply = await selectionReply.ExecuteReaderAsync();
                Assert.True(await reply.ReadAsync());
                Assert.False(reply.IsDBNull(0));
                Assert.True(reply.GetBoolean(1));
            }
            await using var claim = new NpgsqlCommand(
                "SELECT status, claimed FROM notifications.claim_inbound_telegram_agent(@id)",
                worker);
            claim.Parameters.AddWithValue("id", inboundId);
            await using var claimed = await claim.ExecuteReaderAsync();
            Assert.True(await claimed.ReadAsync());
            Assert.Equal("not_admitted", claimed.GetString(0));
            Assert.False(claimed.GetBoolean(1));
        }

        var denied = await SelectAsync(
            factory,
            inboundId,
            Guid.NewGuid(),
            second.CompanyId,
            Guid.NewGuid());
        Assert.Equal("selection_unavailable", denied);
        var selected = await SelectAsync(
            factory,
            inboundId,
            first.RecipientUserId,
            second.CompanyId,
            Guid.NewGuid());
        Assert.Equal("selected", selected);

        var next = await AdmitAsync(factory, Guid.NewGuid(), 2, 2, address);
        Assert.Equal("admitted", next.Resolution);
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM foundation.agent_execution_ledger WHERE inbound_message_id = @id",
            administrator);
        count.Parameters.AddWithValue("id", inboundId);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Technical_receipt_agent_state_and_safe_outbox_are_separate_and_replay_safe()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "830002",
            telegramContactValidated: true);
        await InboundWhatsAppAgentAdapterTests.GrantAgentDocumentCapabilitiesAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId);
        var inboundId = Guid.NewGuid();
        var admission = await AdmitAsync(factory, inboundId, 3, 3, "830002");
        Assert.Equal("admitted", admission.Resolution);

        Guid deliveryId;
        await using (var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await using (var claim = new NpgsqlCommand(
                "SELECT status, claimed FROM notifications.claim_inbound_telegram_agent(@id)",
                worker))
            {
                claim.Parameters.AddWithValue("id", inboundId);
                await using var reader = await claim.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("agent_in_flight", reader.GetString(0));
                Assert.True(reader.GetBoolean(1));
            }
            await using (var enqueue = new NpgsqlCommand(
                "SELECT delivery_id, created FROM notifications.enqueue_inbound_telegram_agent_reply(@id, 'result', 'Resposta segura', NULL)",
                worker))
            {
                enqueue.Parameters.AddWithValue("id", inboundId);
                await using var reader = await enqueue.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                deliveryId = reader.GetGuid(0);
                Assert.True(reader.GetBoolean(1));
            }
            await using var finalize = new NpgsqlCommand(
                "SELECT notifications.finalize_inbound_telegram_agent(@id, 'completed', NULL)",
                worker);
            finalize.Parameters.AddWithValue("id", inboundId);
            Assert.True((bool)(await finalize.ExecuteScalarAsync())!);
        }

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var state = new NpgsqlCommand(
            """
            SELECT message.status, message.agent_status, message.agent_delivery_id,
                   intent.telegram_agent_text, intent.deep_link_selector
            FROM notifications.inbound_telegram_messages AS message
            JOIN notifications.notification_intents AS intent
                ON intent.id = message.agent_delivery_id
            WHERE message.id = @id
            """,
            administrator);
        state.Parameters.AddWithValue("id", inboundId);
        await using var persisted = await state.ExecuteReaderAsync();
        Assert.True(await persisted.ReadAsync());
        Assert.Equal("pending", persisted.GetString(0));
        Assert.Equal("agent_completed", persisted.GetString(1));
        Assert.Equal(deliveryId, persisted.GetGuid(2));
        Assert.Equal("Resposta segura", persisted.GetString(3));
        Assert.True(persisted.IsDBNull(4));
    }

    [Fact]
    public async Task Document_without_draft_create_never_claims_channel_agent()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        const string address = "830003";
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: address,
            telegramContactValidated: true);
        await InboundWhatsAppAgentAdapterTests.GrantAgentDocumentCapabilitiesAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            includePaguaiDraftCreate: false);
        var inboundId = Guid.NewGuid();
        await using (var runtime = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString))
        {
            await runtime.OpenAsync();
            await using var admission = new NpgsqlCommand(
                """
                SELECT status, resolution_state
                FROM notifications.admit_inbound_telegram_message(
                    @id, 4, @chat_id, 4, 'document', NULL,
                    @media_id, 1024, 'application/pdf')
                """,
                runtime);
            admission.Parameters.AddWithValue("id", inboundId);
            admission.Parameters.AddWithValue(
                "chat_id",
                long.Parse(address, System.Globalization.CultureInfo.InvariantCulture));
            admission.Parameters.AddWithValue("media_id", $"media-{inboundId:N}");
            await using var admitted = await admission.ExecuteReaderAsync();
            Assert.True(await admitted.ReadAsync());
            Assert.Equal("created", admitted.GetString(0));
            Assert.Equal("admitted", admitted.GetString(1));
        }

        await using var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var claim = new NpgsqlCommand(
            "SELECT status, claimed FROM notifications.claim_inbound_telegram_agent(@id)",
            worker);
        claim.Parameters.AddWithValue("id", inboundId);
        await using var reader = await claim.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("capability_missing", reader.GetString(0));
        Assert.False(reader.GetBoolean(1));
    }

    [Fact]
    public async Task Membership_revocation_blocks_only_agent_delivery_target()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        const string address = "830004";
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: true,
            telegramAddress: address,
            telegramContactValidated: true);
        await InboundWhatsAppAgentAdapterTests.GrantAgentDocumentCapabilitiesAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId);
        var inboundId = Guid.NewGuid();
        var admission = await AdmitAsync(factory, inboundId, 5, 5, address);
        Assert.Equal("admitted", admission.Resolution);

        Guid agentDeliveryId;
        await using (var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await using (var claim = new NpgsqlCommand(
                "SELECT status, claimed FROM notifications.claim_inbound_telegram_agent(@id)",
                worker))
            {
                claim.Parameters.AddWithValue("id", inboundId);
                await using var claimed = await claim.ExecuteReaderAsync();
                Assert.True(await claimed.ReadAsync());
                Assert.True(claimed.GetBoolean(1));
            }

            await using var enqueue = new NpgsqlCommand(
                "SELECT delivery_id FROM notifications.enqueue_inbound_telegram_agent_reply(@id, 'result', 'Resposta segura', NULL)",
                worker);
            enqueue.Parameters.AddWithValue("id", inboundId);
            agentDeliveryId = (Guid)(await enqueue.ExecuteScalarAsync())!;
        }

        var ordinary = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                TelegramTemplateKey: seed.TelegramTemplateKey));
        var ordinaryDeliveryId = Assert.IsType<Guid>(ordinary.Telegram!.DeliveryId);
        var agentAttemptId = await ClaimDeliveryAsync(factory, agentDeliveryId);
        var ordinaryAttemptId = await ClaimDeliveryAsync(factory, ordinaryDeliveryId);

        await using (var administrator = await factory.Fixture.OpenAdministratorConnectionAsync())
        {
            await using var revoke = new NpgsqlCommand(
                """
                UPDATE foundation.memberships
                SET deleted_at = clock_timestamp(), deleted_by_user_id = @actor,
                    deleted_reason = 'test_revocation'
                WHERE company_id = @company_id AND user_id = @user_id
                    AND deleted_at IS NULL
                """,
                administrator);
            revoke.Parameters.AddWithValue("actor", factory.ActorUserId);
            revoke.Parameters.AddWithValue("company_id", seed.CompanyId);
            revoke.Parameters.AddWithValue("user_id", seed.RecipientUserId);
            Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
        }

        var blockedAgent = await ReadTelegramTargetAsync(
            factory,
            agentDeliveryId,
            agentAttemptId);
        Assert.Null(blockedAgent.ChatId);
        Assert.Null(blockedAgent.MessageText);

        var ordinaryTarget = await ReadTelegramTargetAsync(
            factory,
            ordinaryDeliveryId,
            ordinaryAttemptId);
        Assert.Equal(address, ordinaryTarget.ChatId);
        Assert.Equal(seed.TelegramText, ordinaryTarget.MessageText);
    }

    [Fact]
    public async Task Completed_ledger_redelivery_enqueues_one_intent_without_model_call()
    {
        var model = new InboundWhatsAppAgentAdapterTests.NeverCalledAgentModel();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            agentModel: model,
            agentRunOptions: InboundWhatsAppAgentAdapterTests.ConfiguredAgentOptions());
        const string address = "830005";
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: address,
            telegramContactValidated: true);
        var membershipId = await InboundWhatsAppAgentAdapterTests.GrantAgentDocumentCapabilitiesAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId);
        var inboundId = Guid.NewGuid();
        await using (var runtime = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString))
        {
            await runtime.OpenAsync();
            await using var admission = new NpgsqlCommand(
                """
                SELECT status, resolution_state
                FROM notifications.admit_inbound_telegram_message(
                    @id, 6, @chat_id, 6, 'photo', 'Analise o anexo sintético.',
                    @media_id, 1024, 'image/jpeg')
                """,
                runtime);
            admission.Parameters.AddWithValue("id", inboundId);
            admission.Parameters.AddWithValue(
                "chat_id",
                long.Parse(address, System.Globalization.CultureInfo.InvariantCulture));
            admission.Parameters.AddWithValue("media_id", $"media-{inboundId:N}");
            await using var admitted = await admission.ExecuteReaderAsync();
            Assert.True(await admitted.ReadAsync());
            Assert.Equal("created", admitted.GetString(0));
            Assert.Equal("admitted", admitted.GetString(1));
        }

        await using (var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await using var claim = new NpgsqlCommand(
                "SELECT claimed FROM notifications.claim_inbound_telegram_agent(@id)",
                worker);
            claim.Parameters.AddWithValue("id", inboundId);
            Assert.True((bool)(await claim.ExecuteScalarAsync())!);
        }

        var fileId = await InboundWhatsAppAgentAdapterTests.SeedConfirmedAgentFileAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            membershipId,
            inboundId);
        await using (var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await using var record = new NpgsqlCommand(
                "SELECT notifications.record_inbound_telegram_agent_file(@id, @file_id)",
                worker);
            record.Parameters.AddWithValue("id", inboundId);
            record.Parameters.AddWithValue("file_id", fileId);
            Assert.True((bool)(await record.ExecuteScalarAsync())!);
        }

        var actor = new Vinx.Erp.Foundation.Identity.ActorContext(seed.RecipientUserId, Guid.NewGuid());
        var command = new AgentCommand(
            actor,
            "telegram",
            inboundId,
            inboundId,
            AgentTaskProfileCatalog.ReadOnlySummarizeProfileId,
            "Analise o anexo sintético.",
            seed.CompanyId,
            [fileId]);
        await InboundWhatsAppAgentAdapterTests.SeedCompletedLedgerAsync(
            factory,
            command,
            "Resposta segura");

        await using (var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await using var recovery = new NpgsqlCommand(
                "SELECT status, claimed, agent_file_id FROM notifications.claim_inbound_telegram_agent(@id)",
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
        Assert.Equal(0, model.CallCount);

        Guid deliveryId;
        await using (var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await using var enqueue = new NpgsqlCommand(
                "SELECT delivery_id FROM notifications.enqueue_inbound_telegram_agent_reply(@id, 'result', 'Resposta segura', NULL)",
                worker);
            enqueue.Parameters.AddWithValue("id", inboundId);
            deliveryId = (Guid)(await enqueue.ExecuteScalarAsync())!;

            await using var finalize = new NpgsqlCommand(
                "SELECT notifications.finalize_inbound_telegram_agent(@id, 'completed', NULL)",
                worker);
            finalize.Parameters.AddWithValue("id", inboundId);
            Assert.True((bool)(await finalize.ExecuteScalarAsync())!);

            await using var replay = new NpgsqlCommand(
                "SELECT claimed FROM notifications.claim_inbound_telegram_agent(@id)",
                worker);
            replay.Parameters.AddWithValue("id", inboundId);
            Assert.False((bool)(await replay.ExecuteScalarAsync())!);
        }

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM notifications.notification_intents WHERE id = @delivery_id OR event_key = @event_key",
            administrator);
        count.Parameters.AddWithValue("delivery_id", deliveryId);
        count.Parameters.AddWithValue("event_key", $"telegram-agent:{inboundId}:result");
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    private static async Task<Guid> ClaimDeliveryAsync(
        FoundationApiFactory factory,
        Guid deliveryId)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using (var accept = new NpgsqlCommand(
            "SELECT status FROM notifications.accept_notification_delivery(@id, @correlation)",
            administrator))
        {
            accept.Parameters.AddWithValue("id", deliveryId);
            accept.Parameters.AddWithValue("correlation", Guid.NewGuid());
            Assert.Equal("pending_provider", (string)(await accept.ExecuteScalarAsync())!);
        }

        await using var claim = new NpgsqlCommand(
            "SELECT status, attempt_id FROM notifications.claim_notification_delivery(@id)",
            administrator);
        claim.Parameters.AddWithValue("id", deliveryId);
        await using var reader = await claim.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("provider_in_flight", reader.GetString(0));
        return reader.GetGuid(1);
    }

    private static async Task<(string? ChatId, string? MessageText)> ReadTelegramTargetAsync(
        FoundationApiFactory factory,
        Guid deliveryId,
        Guid attemptId)
    {
        await using var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT chat_id, message_text FROM notifications.get_telegram_notification_delivery_target(@id, @attempt)",
            worker);
        command.Parameters.AddWithValue("id", deliveryId);
        command.Parameters.AddWithValue("attempt", attemptId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task<(string Status, string Resolution, Guid? DeliveryId)> AdmitAsync(
        FoundationApiFactory factory,
        Guid inboundId,
        long updateId,
        long messageId,
        string address)
    {
        await using var runtime = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT status, resolution_state, delivery_id
            FROM notifications.admit_inbound_telegram_message(
                @id, @update_id, @chat_id, @message_id,
                'text', 'Mensagem sintética', NULL, NULL, NULL)
            """,
            runtime);
        command.Parameters.AddWithValue("id", inboundId);
        command.Parameters.AddWithValue("update_id", updateId);
        command.Parameters.AddWithValue("chat_id", long.Parse(address, System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("message_id", messageId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2));
    }

    private static async Task<string> SelectAsync(
        FoundationApiFactory factory,
        Guid inboundId,
        Guid actorUserId,
        Guid companyId,
        Guid requestId)
    {
        await using var runtime = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT notifications.select_agent_channel_company(@inbound_id, @actor, @company, @request)",
            runtime);
        command.Parameters.AddWithValue("inbound_id", inboundId);
        command.Parameters.AddWithValue("actor", actorUserId);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("request", requestId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task RebindCompanyToUserAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid oldUserId,
        Guid linkedUserId,
        string address)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM notifications.notification_recipient_contacts
            WHERE company_id = @company_id AND user_id = @old_user_id AND channel = 'telegram';
            DELETE FROM foundation.memberships
            WHERE company_id = @company_id AND user_id = @old_user_id;
            INSERT INTO foundation.memberships (
                id, company_id, user_id, created_by_user_id, updated_by_user_id,
                request_id, created_at, updated_at)
            VALUES (
                gen_random_uuid(), @company_id, @linked_user_id, @actor, @actor,
                gen_random_uuid(), clock_timestamp(), clock_timestamp());
            INSERT INTO notifications.notification_recipient_contacts (
                company_id, user_id, channel, address, is_validated, updated_by_user_id)
            VALUES (@company_id, @linked_user_id, 'telegram', @address, true, @linked_user_id);
            """,
            administrator);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("old_user_id", oldUserId);
        command.Parameters.AddWithValue("linked_user_id", linkedUserId);
        command.Parameters.AddWithValue("actor", factory.ActorUserId);
        command.Parameters.AddWithValue("address", address);
        await command.ExecuteNonQueryAsync();
    }
}
