using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Notifications;

namespace Vinx.Erp.WorkerHost;

/// <summary>Fecha a janela curta de correlação sem processar texto isolado.</summary>
[InboundWhatsAppFailurePolicy]
public sealed class InboundWhatsAppTextCorrelationExpiredHandler(
    FoundationDbContext database,
    InboundAgentChannelAdapter? adapter,
    ILogger<InboundWhatsAppTextCorrelationExpiredHandler> logger
)
{
    public InboundWhatsAppTextCorrelationExpiredHandler(
        FoundationDbContext database,
        ILogger<InboundWhatsAppTextCorrelationExpiredHandler> logger)
        : this(database, null, logger)
    {
    }

    public async Task Handle(
        InboundWhatsAppTextCorrelationExpired message,
        CancellationToken cancellationToken
    )
    {
        if (message.InboundMessageId == Guid.Empty)
        {
            throw new InboundWhatsAppDeadLetterException(
                InboundWhatsAppDeadLetterException.InvalidExceptionType,
                "Inbound WhatsApp text expiry rejected the envelope."
            );
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        var connection = database.Database.GetDbConnection();
        var correlation = await ExecuteAsync<string>(
            connection,
            "SELECT notifications.correlate_inbound_whatsapp_text_image(@inbound_id)",
            message.InboundMessageId,
            cancellationToken
        );
        if (adapter is null)
        {
            var expired = await ExecuteAsync<bool>(
                connection,
                "SELECT notifications.expire_inbound_whatsapp_text(@inbound_id)",
                message.InboundMessageId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Inbound text expiry {Operation} {Module} {Result} {Reason}",
                    "whatsapp_inbound_text_expiry",
                    "notifications",
                    expired ? "expired" : correlation == "correlated" ? "correlated" : "noop",
                    expired ? "deadline_elapsed" : correlation == "correlated" ? "unique_pair" : "replay_or_not_due");
            }

            return;
        }

        await transaction.CommitAsync(cancellationToken);

        if (!string.Equals(correlation, "correlated", StringComparison.Ordinal))
        {
            await adapter.ProcessAsync(
                new InboundAgentRequestReceived(
                    message.InboundMessageId,
                    AgentChannelKind.WhatsApp),
                cancellationToken);
        }

        var result = correlation == "correlated" ? "correlated" : "forwarded";
        var reason = correlation == "correlated" ? "unique_pair" : "agent_adapter";
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Inbound text expiry {Operation} {Module} {Result} {Reason}",
                "whatsapp_inbound_text_expiry",
                "notifications",
                result,
                reason
            );
        }
    }

    private async Task<T> ExecuteAsync<T>(
        System.Data.Common.DbConnection connection,
        string commandText,
        Guid inboundMessageId,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = commandText;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "inbound_id";
        parameter.Value = inboundMessageId;
        command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            throw new InvalidOperationException("Inbound WhatsApp expiry returned no result.");
        }

        return (T)Convert.ChangeType(
            value,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture
        );
    }
}
