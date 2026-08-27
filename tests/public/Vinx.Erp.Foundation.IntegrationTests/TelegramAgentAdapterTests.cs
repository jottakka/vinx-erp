using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vinx.Erp.ApiHost.Notifications;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
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
