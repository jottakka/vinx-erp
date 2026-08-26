using Npgsql;
using NpgsqlTypes;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Contrato da confirmação técnica da fila Telegram: <c>completed</c> é apenas
/// o recibo do worker e nunca uma confirmação para a pessoa remetente.
/// </summary>
[Collection("notification-worker")]
public sealed class TelegramInboundWorkerTests
{
    [Fact]
    public async Task Admitted_pending_message_completes_once_as_a_queue_receipt()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "910001",
            telegramContactValidated: true
        );
        var resolution = await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 910001);
        var inboundId = Guid.NewGuid();

        Assert.Equal(
            "created",
            await AdmitAsync(
                factory.Fixture.RuntimeConnectionString,
                inboundId,
                resolution,
                "receipt-only text"
            )
        );

        var first = await CompleteAsync(factory.Fixture.WorkerConnectionString, inboundId);
        var replay = await CompleteAsync(factory.Fixture.WorkerConnectionString, inboundId);

        Assert.Equal(new TelegramInboundReceipt("completed", true), first);
        Assert.Equal(new TelegramInboundReceipt("completed", false), replay);
        var row = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal(inboundId, row.Id);
        Assert.Equal("receipt-only text", row.TextBody);
        Assert.Equal("completed", row.Status);
        Assert.NotNull(row.CompletedAt);
        Assert.Equal(seed.CompanyId, row.ResolvedCompanyId);
    }

    private static async Task<string> AdmitAsync(
        string connectionString,
        Guid inboundId,
        TelegramInboundActorResolution resolution,
        string textBody
    )
    {
        await using var runtime = new NpgsqlConnection(connectionString);
        await runtime.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT notifications.admit_inbound_telegram_text(
                @inbound_id, @update_id, @chat_id, @message_id, @text_body,
                @resolution_state, @user_id, @company_id, @membership_id)
            """,
            runtime
        );
        command.Parameters.AddWithValue("inbound_id", NpgsqlDbType.Uuid, inboundId);
        command.Parameters.AddWithValue("update_id", NpgsqlDbType.Bigint, 91L);
        command.Parameters.AddWithValue("chat_id", NpgsqlDbType.Bigint, 910001L);
        command.Parameters.AddWithValue("message_id", NpgsqlDbType.Bigint, 9101L);
        command.Parameters.AddWithValue("text_body", NpgsqlDbType.Text, textBody);
        command.Parameters.AddWithValue("resolution_state", NpgsqlDbType.Text, resolution.ResolutionState);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Uuid, resolution.UserId!.Value);
        command.Parameters.AddWithValue("company_id", NpgsqlDbType.Uuid, resolution.CompanyId!.Value);
        command.Parameters.AddWithValue("membership_id", NpgsqlDbType.Uuid, resolution.MembershipId!.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<TelegramInboundReceipt> CompleteAsync(
        string connectionString,
        Guid inboundId
    )
    {
        await using var worker = new NpgsqlConnection(connectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status, transitioned FROM notifications.complete_inbound_telegram_text(@inbound_id)",
            worker
        );
        command.Parameters.AddWithValue("inbound_id", NpgsqlDbType.Uuid, inboundId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = new TelegramInboundReceipt(reader.GetString(0), reader.GetBoolean(1));
        Assert.False(await reader.ReadAsync());
        return result;
    }

    private sealed record TelegramInboundReceipt(string Status, bool Transitioned);
}
