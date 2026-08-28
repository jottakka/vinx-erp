using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Notifications;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace Vinx.Erp.ApiHost.Notifications;

/// <summary>
/// Admite texto privado Telegram com a resolução fechada e publica somente
/// mensagens novas admitidas na mesma transação do outbox durável.
/// </summary>
public sealed class InboundTelegramIngressService(
    FoundationDbContext database,
    IDbContextOutbox outbox,
    ILogger<InboundTelegramIngressService> logger
)
{
    private const string Operation = "telegram_inbound_ingress";
    private const string Module = "notifications";

    public async Task<bool> RecordAsync(
        InboundTelegramText text,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(text);

        var inboundId = Guid.NewGuid();
        var committedByOutbox = false;
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );

        var connection = database.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            "SELECT status, resolution_state, delivery_id "
            + "FROM notifications.admit_inbound_telegram_message("
            + "@inbound_id, @update_id, @chat_id, @message_id, @message_type, "
            + "@text_body, @media_id, @media_size, @media_mime_type)";
        AddParameter(command, "inbound_id", inboundId);
        AddParameter(command, "update_id", text.UpdateId);
        AddParameter(command, "chat_id", text.ChatId);
        AddParameter(command, "message_id", text.MessageId);
        AddParameter(command, "message_type", text.MessageType);
        AddParameter(command, "text_body", (object?)text.Body ?? DBNull.Value);
        AddParameter(command, "media_id", (object?)text.MediaId ?? DBNull.Value);
        AddParameter(command, "media_size", (object?)text.MediaSize ?? DBNull.Value);
        AddParameter(command, "media_mime_type", (object?)text.MediaType ?? DBNull.Value);

        string admission;
        string resolution;
        bool admitted;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "Inbound telegram text admission returned no row."
                );
            }

            admission = reader.GetString(0);
            resolution = reader.GetString(1);
            admitted = string.Equals(
                resolution,
                "admitted",
                StringComparison.Ordinal
            );
            if (await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "Inbound telegram text admission returned multiple rows."
                );
            }
        }

        var created = string.Equals(admission, "created", StringComparison.Ordinal);

        if (!created && !string.Equals(admission, "replay", StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Inbound ingress {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "conflict",
                "message_id_conflict"
            );
        }

        if (created
            && (admitted
                || string.Equals(resolution, "selection_required", StringComparison.Ordinal)))
        {
            outbox.Enroll(database);
            var correlationId = Guid.NewGuid().ToString("D");
            await outbox.PublishAsync(
                new InboundTelegramTextReceived(inboundId),
                new DeliveryOptions { CorrelationId = correlationId }
            );
            await outbox.SaveChangesAndFlushMessagesAsync(
                MultiFlushMode.AllowMultiples,
                cancellationToken
            );
            committedByOutbox = true;
        }

        if (!committedByOutbox)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return created;
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
