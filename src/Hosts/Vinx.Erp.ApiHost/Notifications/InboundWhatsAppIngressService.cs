using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Notifications;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vinx.Erp.ApiHost.Notifications;

/// <summary>Imagem inbound extraída do webhook: ids do provider e legenda opcional.</summary>
public sealed record InboundWhatsAppImage(
    string MessageId,
    string WaId,
    string MediaId,
    string? Caption
);

/// <summary>
/// Registra a imagem inbound deduplicada e publica o evento Wolverine na
/// mesma transação do produtor (padrão do NotificationIntentService): replay
/// do webhook não gera novo processamento.
/// </summary>
public sealed class InboundWhatsAppIngressService(
    FoundationDbContext database,
    IDbContextOutbox outbox,
    ILogger<InboundWhatsAppIngressService> logger
)
{
    private const string Operation = "whatsapp_inbound_ingress";
    private const string Module = "notifications";

    public async Task<bool> RecordAsync(
        InboundWhatsAppImage image,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(image);

        var inboundId = Guid.NewGuid();
        var commitAttempted = false;
        var created = false;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            var connection = database.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                "SELECT notifications.record_inbound_whatsapp_message(@inbound_id, @message_id, @wa_id, @media_id, @caption)";
            AddParameter(command, "inbound_id", inboundId);
            AddParameter(command, "message_id", image.MessageId);
            AddParameter(command, "wa_id", image.WaId);
            AddParameter(command, "media_id", image.MediaId);
            AddParameter(command, "caption", image.Caption is null ? DBNull.Value : image.Caption);
            var status = (string?)await command.ExecuteScalarAsync(cancellationToken);
            created = string.Equals(status, "created", StringComparison.Ordinal);
            if (created)
            {
                outbox.Enroll(database);
                await outbox.PublishAsync(
                    new InboundWhatsAppImageReceived(inboundId),
                    new DeliveryOptions { CorrelationId = Guid.NewGuid().ToString("D") }
                );
            }

            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
            when (!commitAttempted && exception.SqlState == PostgresErrorCodes.InvalidParameterValue)
        {
            // Payload aceito pelo provider mas inválido para o contrato interno:
            // ignora sem quebrar o webhook (as demais mensagens do POST seguem).
            logger.LogWarning(
                "Inbound ingress {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "ignored",
                "invalid_parameters"
            );
            return false;
        }

        if (created)
        {
            try
            {
                await outbox.FlushOutgoingMessagesAsync();
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Inbound ingress {Operation} {Module} {Result} {Reason}",
                    Operation,
                    Module,
                    "enqueued",
                    "outbox_flush_deferred"
                );
            }
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
