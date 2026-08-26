extern alias workerhost;

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Notifications;
using Wolverine;
using Wolverine.Persistence.Durability;
using Xunit;
using WorkerHostTypes = workerhost::Vinx.Erp.WorkerHost;

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

    [Fact]
    public async Task Closed_and_missing_messages_return_stable_noop_receipts()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var closedId = Guid.NewGuid();

        Assert.Equal(
            "created",
            await AdmitClosedAsync(factory.Fixture.RuntimeConnectionString, closedId)
        );

        var closed = await CompleteAsync(factory.Fixture.WorkerConnectionString, closedId);
        var missing = await CompleteAsync(factory.Fixture.WorkerConnectionString, Guid.NewGuid());

        Assert.Equal(new TelegramInboundReceipt("not_admitted", false), closed);
        Assert.Equal(new TelegramInboundReceipt("not_found", false), missing);
        var row = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal(closedId, row.Id);
        Assert.Equal("pending", row.Status);
        Assert.Null(row.CompletedAt);
    }

    [Fact]
    public async Task Handler_completes_an_admitted_message_inside_the_active_ef_transaction()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "910002",
            telegramContactValidated: true
        );
        var resolution = await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 910002);
        var inboundId = Guid.NewGuid();
        Assert.Equal(
            "created",
            await AdmitAsync(
                factory.Fixture.RuntimeConnectionString,
                inboundId,
                resolution,
                "transaction receipt text"
            )
        );

        var logger = new CapturingLogger<WorkerHostTypes.InboundTelegramTextReceivedHandler>();
        await using (var database = CreateWorkerDatabase(factory))
        {
            await using var transaction = await database.Database.BeginTransactionAsync();
            var handler = new WorkerHostTypes.InboundTelegramTextReceivedHandler(database, logger);
            await handler.Handle(
                new InboundTelegramTextReceived(inboundId),
                CancellationToken.None
            );
            await transaction.CommitAsync();
        }

        var row = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal("completed", row.Status);
        Assert.NotNull(row.CompletedAt);
        var decision = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, decision.Level);
        Assert.Equal("telegram_inbound_receipt", decision.Properties["Operation"]);
        Assert.Equal("notifications", decision.Properties["Module"]);
        Assert.Equal("completed", decision.Properties["Result"]);
        Assert.Equal("queue_receipt", decision.Properties["Reason"]);
        Assert.DoesNotContain(inboundId.ToString("D"), decision.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(seed.CompanyId, row.ResolvedCompanyId);
    }

    [Fact]
    public async Task Handler_opens_a_closed_ef_connection_for_an_admitted_message()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "910003",
            telegramContactValidated: true
        );
        var resolution = await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 910003);
        var inboundId = Guid.NewGuid();
        Assert.Equal(
            "created",
            await AdmitAsync(
                factory.Fixture.RuntimeConnectionString,
                inboundId,
                resolution,
                "closed connection receipt text"
            )
        );

        await using (var database = CreateWorkerDatabase(factory))
        {
            Assert.Equal(System.Data.ConnectionState.Closed, database.Database.GetDbConnection().State);
            var handler = new WorkerHostTypes.InboundTelegramTextReceivedHandler(
                database,
                new CapturingLogger<WorkerHostTypes.InboundTelegramTextReceivedHandler>()
            );
            await handler.Handle(
                new InboundTelegramTextReceived(inboundId),
                CancellationToken.None
            );
        }

        var row = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal("completed", row.Status);
        Assert.NotNull(row.CompletedAt);
        Assert.Equal(seed.CompanyId, row.ResolvedCompanyId);
    }

    [Fact]
    public async Task Registered_dead_letter_interceptor_redacts_a_telegram_envelope_without_changing_its_stable_exception()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString
        );
        using var client = worker.CreateClient();
        var originalId = Guid.NewGuid();
        var envelope = new Envelope(new InboundTelegramTextReceived(originalId))
        {
            Data = Encoding.UTF8.GetBytes($"{{\"inboundMessageId\":\"{originalId:D}\",\"payload\":\"secret\"}}"),
        };
        envelope.Headers["diagnostic"] = "secret-header";
        var originalException = new WorkerHostTypes.InboundTelegramDeadLetterException(
            WorkerHostTypes.InboundTelegramDeadLetterException.InvalidExceptionType,
            "Inbound Telegram receipt rejected the envelope."
        );
        var interceptor = worker
            .Services.GetRequiredService<IEnumerable<IDeadLetterInterceptor>>()
            .Single();

        var persistedException = await interceptor.BeforeStoreAsync(
            envelope,
            originalException,
            CancellationToken.None
        );

        var telegramException = Assert.IsType<WorkerHostTypes.InboundTelegramDeadLetterException>(
            persistedException
        );
        Assert.Equal(
            WorkerHostTypes.InboundTelegramDeadLetterException.InvalidExceptionType,
            telegramException.ExceptionType
        );
        Assert.Equal("Inbound Telegram receipt rejected the envelope.", telegramException.Message);
        Assert.Empty(envelope.Headers);
        var data = Encoding.UTF8.GetString(envelope.Data!);
        Assert.DoesNotContain(originalId.ToString("D"), data, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", data, StringComparison.OrdinalIgnoreCase);
        using var payload = JsonDocument.Parse(data);
        Assert.Equal(Guid.Empty, payload.RootElement.GetProperty("inboundMessageId").GetGuid());
    }

    [Fact]
    public async Task Registered_dead_letter_interceptor_replaces_untrusted_telegram_exception_content()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString
        );
        using var client = worker.CreateClient();
        var secret = "telegram-provider-id-secret";
        var envelope = new Envelope(new InboundTelegramTextReceived(Guid.NewGuid()));
        var interceptor = worker
            .Services.GetRequiredService<IEnumerable<IDeadLetterInterceptor>>()
            .Single();

        var persistedException = await interceptor.BeforeStoreAsync(
            envelope,
            new WorkerHostTypes.InboundTelegramDeadLetterException("untrusted", secret),
            CancellationToken.None
        );

        var telegramException = Assert.IsType<WorkerHostTypes.InboundTelegramDeadLetterException>(
            persistedException
        );
        Assert.Equal(
            WorkerHostTypes.InboundTelegramDeadLetterException.UnexpectedExceptionType,
            telegramException.ExceptionType
        );
        Assert.Equal("Inbound Telegram receipt failed unexpectedly.", telegramException.Message);
        Assert.DoesNotContain(secret, telegramException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, telegramException.ToString(), StringComparison.Ordinal);
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

    private static async Task<string> AdmitClosedAsync(string connectionString, Guid inboundId)
    {
        await using var runtime = new NpgsqlConnection(connectionString);
        await runtime.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT notifications.admit_inbound_telegram_text(
                @inbound_id, @update_id, @chat_id, @message_id, @text_body,
                'unbound', NULL, NULL, NULL)
            """,
            runtime
        );
        command.Parameters.AddWithValue("inbound_id", NpgsqlDbType.Uuid, inboundId);
        command.Parameters.AddWithValue("update_id", NpgsqlDbType.Bigint, 92L);
        command.Parameters.AddWithValue("chat_id", NpgsqlDbType.Bigint, 920001L);
        command.Parameters.AddWithValue("message_id", NpgsqlDbType.Bigint, 9201L);
        command.Parameters.AddWithValue("text_body", NpgsqlDbType.Text, "closed receipt text");
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

    private static FoundationDbContext CreateWorkerDatabase(FoundationApiFactory factory) =>
        new(
            new DbContextOptionsBuilder<FoundationDbContext>()
                .UseNpgsql(factory.Fixture.WorkerConnectionString)
                .UseSnakeCaseNamingConvention()
                .Options
        );

    private sealed record TelegramInboundReceipt(string Status, bool Transitioned);
}
