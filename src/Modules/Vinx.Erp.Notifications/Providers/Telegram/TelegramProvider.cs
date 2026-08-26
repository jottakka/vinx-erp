using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Notifications.Providers.Telegram;

/// <summary>
/// Provider do canal Telegram: lê apenas o alvo da tentativa em transação
/// curta e a encerra antes de chamar o Bot API.
/// </summary>
public sealed class TelegramProvider(
    FoundationDbContext database,
    IHttpClientFactory httpClientFactory,
    TelegramOptions options,
    ILogger<TelegramProvider> logger
) : INotificationProvider
{
    public string Channel => NotificationChannel.Telegram;

    public async Task<NotificationProviderResult> DeliverAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.DeliveryId == Guid.Empty || request.AttemptId == Guid.Empty)
        {
            return Rejected();
        }

        if (!options.OutboundIsConfigured)
        {
            logger.LogWarning(
                "Telegram delivery {Operation} {Result} {Reason}",
                "telegram_delivery",
                "rejected",
                "provider_unconfigured"
            );
            return Rejected();
        }

        var target = await ReadTargetAsync(request, cancellationToken);
        if (target is null)
        {
            logger.LogWarning(
                "Telegram delivery {Operation} {Result} {Reason}",
                "telegram_delivery",
                "rejected",
                "delivery_target_missing"
            );
            return Rejected();
        }

        TelegramMessage message;
        try
        {
            message = TelegramMapper.Map(target.ChatId, target.MessageText);
        }
        catch (ArgumentException)
        {
            logger.LogWarning(
                "Telegram delivery {Operation} {Result} {Reason}",
                "telegram_delivery",
                "rejected",
                "delivery_target_invalid"
            );
            return Rejected();
        }

        var client = new TelegramClient(
            httpClientFactory.CreateClient(TelegramClient.HttpClientName),
            options
        );
        return await client.SendAsync(message, cancellationToken);
    }

    private async Task<DeliveryTarget?> ReadTargetAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        var connection = database.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            SELECT chat_id, message_text
            FROM notifications.get_telegram_notification_delivery_target(@delivery_id, @attempt_id)
            """;
        AddParameter(command, "delivery_id", request.DeliveryId);
        AddParameter(command, "attempt_id", request.AttemptId);

        DeliveryTarget? target = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (
                await reader.ReadAsync(cancellationToken)
                && !reader.IsDBNull(0)
                && !reader.IsDBNull(1)
            )
            {
                target = new DeliveryTarget(reader.GetString(0), reader.GetString(1));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return target;
    }

    private static NotificationProviderResult Rejected() =>
        new(NotificationProviderResultKind.Rejected);

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record DeliveryTarget(string ChatId, string MessageText);
}
