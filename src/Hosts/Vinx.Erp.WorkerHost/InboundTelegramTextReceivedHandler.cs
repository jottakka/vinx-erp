using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Notifications;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;

namespace Vinx.Erp.WorkerHost;

/// <summary>Registra o recibo técnico da fila Telegram sem ler ou processar texto.</summary>
[InboundTelegramFailurePolicy]
public sealed class InboundTelegramTextReceivedHandler(
    FoundationDbContext database,
    ILogger<InboundTelegramTextReceivedHandler> logger,
    InboundAgentChannelAdapter? adapter = null,
    IMessageBus? bus = null
)
{
    private const string Operation = "telegram_inbound_receipt";
    private const string Module = "notifications";

    public async Task Handle(
        InboundTelegramTextReceived message,
        CancellationToken cancellationToken
    )
    {
        if (message.InboundMessageId == Guid.Empty)
        {
            throw new InboundTelegramDeadLetterException(
                InboundTelegramDeadLetterException.InvalidExceptionType,
                "Inbound Telegram receipt rejected the envelope."
            );
        }

        TelegramInboundReceipt receipt;
        try
        {
            receipt = await CompleteAsync(message.InboundMessageId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NpgsqlException exception) when (exception.IsTransient)
        {
            throw new InboundTelegramReceiptExecutionException(isTransient: true);
        }
        catch (Exception)
        {
            throw new InboundTelegramReceiptExecutionException(isTransient: false);
        }

        var (result, reason) = receipt switch
        {
            { Status: "completed", Transitioned: true } => ("completed", "queue_receipt"),
            { Status: "completed", Transitioned: false } => ("noop", "replay"),
            { Status: "not_found", Transitioned: false } => ("noop", "not_found"),
            { Status: "not_admitted", Transitioned: false } => ("noop", "not_admitted"),
            _ => throw new InvalidOperationException("Inbound Telegram receipt returned an unknown state."),
        };

        Guid? deliveryId = null;
        if (receipt.Status == "completed")
        {
            deliveryId = await EnqueueSelectionDeliveryAsync(
                message.InboundMessageId,
                cancellationToken).ConfigureAwait(false);
            if (deliveryId is null && adapter is not null)
            {
                try
                {
                    await adapter.ProcessAsync(
                        new InboundAgentRequestReceived(
                            message.InboundMessageId,
                            AgentChannelKind.Telegram),
                        cancellationToken).ConfigureAwait(false);
                    deliveryId = await ReadAgentDeliveryIdAsync(
                        message.InboundMessageId,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (NotificationProviderTransientException)
                {
                    throw new InboundTelegramReceiptExecutionException(isTransient: true);
                }
            }
        }
        else if (receipt.Status == "not_admitted")
        {
            deliveryId = await EnqueueSelectionDeliveryAsync(
                message.InboundMessageId,
                cancellationToken).ConfigureAwait(false);
        }

        if (deliveryId is { } opaqueDeliveryId && bus is not null)
        {
            await bus.PublishAsync(
                new NotificationDeliveryRequested(opaqueDeliveryId),
                new DeliveryOptions { CorrelationId = Guid.NewGuid().ToString("D") })
                .ConfigureAwait(false);
        }

#pragma warning disable CA1873 // Os argumentos são valores constantes e baratos; o guard não agrega ganho.
        logger.LogInformation(
            "Inbound Telegram receipt {Operation} {Module} {Result} {Reason}",
            Operation,
            Module,
            result,
            reason
        );
#pragma warning restore CA1873
    }

    internal static async ValueTask PersistRedactedWorkerDeadLetterAsync(
        IWolverineRuntime runtime,
        IEnvelopeLifecycle lifecycle,
        Exception _,
        string exceptionType,
        string exceptionMessage
    )
    {
        var envelope =
            lifecycle.Envelope
            ?? throw new InvalidOperationException("Worker dead-letter lifecycle has no envelope.");

        WorkerDeadLetterRedactionInterceptor.RedactEnvelope(envelope);
        await runtime.Storage.Inbox.MoveToDeadLetterStorageAsync(
            envelope,
            new InboundTelegramDeadLetterException(exceptionType, exceptionMessage)
        );
        await lifecycle.CompleteAsync();
    }

    internal static ValueTask PersistInvalidDeadLetterAsync(
        IWolverineRuntime runtime,
        IEnvelopeLifecycle lifecycle,
        Exception exception
    ) =>
        PersistRedactedWorkerDeadLetterAsync(
            runtime,
            lifecycle,
            exception,
            InboundTelegramDeadLetterException.InvalidExceptionType,
            "Inbound Telegram receipt rejected the envelope."
        );

    internal static ValueTask PersistRetryExhaustedDeadLetterAsync(
        IWolverineRuntime runtime,
        IEnvelopeLifecycle lifecycle,
        Exception exception
    ) =>
        PersistRedactedWorkerDeadLetterAsync(
            runtime,
            lifecycle,
            exception,
            InboundTelegramDeadLetterException.RetryExhaustedExceptionType,
            "Inbound Telegram receipt exhausted transient retries."
        );

    internal static ValueTask PersistUnexpectedDeadLetterAsync(
        IWolverineRuntime runtime,
        IEnvelopeLifecycle lifecycle,
        Exception exception
    ) =>
        PersistRedactedWorkerDeadLetterAsync(
            runtime,
            lifecycle,
            exception,
            InboundTelegramDeadLetterException.UnexpectedExceptionType,
            "Inbound Telegram receipt failed unexpectedly."
        );

    private async Task<TelegramInboundReceipt> CompleteAsync(
        Guid inboundMessageId,
        CancellationToken cancellationToken
    )
    {
        var connection = database.Database.GetDbConnection();
        var openedConnection = connection.State != ConnectionState.Open;
        if (openedConnection)
        {
            await database.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                "SELECT status, transitioned FROM notifications.complete_inbound_telegram_text(@inbound_id)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "inbound_id";
            parameter.Value = inboundMessageId;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Inbound Telegram receipt returned no result.");
            }

            var receipt = new TelegramInboundReceipt(reader.GetString(0), reader.GetBoolean(1));
            if (await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Inbound Telegram receipt returned multiple results.");
            }

            return receipt;
        }
        finally
        {
            if (openedConnection)
            {
                await database.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task<Guid?> ReadAgentDeliveryIdAsync(
        Guid inboundMessageId,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        var openedConnection = connection.State != ConnectionState.Open;
        if (openedConnection)
        {
            await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                "SELECT notifications.get_inbound_telegram_agent_delivery(@inbound_id)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "inbound_id";
            parameter.Value = inboundMessageId;
            command.Parameters.Add(parameter);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is Guid deliveryId ? deliveryId : null;
        }
        finally
        {
            if (openedConnection)
            {
                await database.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<Guid?> EnqueueSelectionDeliveryAsync(
        Guid inboundMessageId,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        var openedConnection = connection.State != ConnectionState.Open;
        if (openedConnection)
        {
            await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                "SELECT delivery_id FROM notifications.enqueue_inbound_telegram_selection_reply(@inbound_id)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "inbound_id";
            parameter.Value = inboundMessageId;
            command.Parameters.Add(parameter);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is Guid opaqueDeliveryId ? opaqueDeliveryId : null;
        }
        finally
        {
            if (openedConnection)
            {
                await database.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed record TelegramInboundReceipt(string Status, bool Transitioned);
}
