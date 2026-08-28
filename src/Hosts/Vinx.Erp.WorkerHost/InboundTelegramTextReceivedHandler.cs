using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Notifications;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;

namespace Vinx.Erp.WorkerHost;

/// <summary>Registra o recibo técnico da fila Telegram sem ler ou processar texto.</summary>
[InboundTelegramFailurePolicy]
public sealed class InboundTelegramTextReceivedHandler(
    FoundationDbContext database,
    ILogger<InboundTelegramTextReceivedHandler> logger,
    InboundAgentChannelAdapter? adapter,
    IDbContextOutbox outbox
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

        if (receipt.Status == "completed")
        {
            var deliveryId = await EnqueueSelectionDeliveryAsync(
                message.InboundMessageId,
                cancellationToken).ConfigureAwait(false);
            if (deliveryId is null && adapter is not null)
            {
                try
                {
                    // O adapter publica o envelope de entrega na mesma transação
                    // do intent de reply; sem envelope órfão em caso de falha.
                    await adapter.ProcessAsync(
                        new InboundAgentRequestReceived(
                            message.InboundMessageId,
                            AgentChannelKind.Telegram),
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
            _ = await EnqueueSelectionDeliveryAsync(
                message.InboundMessageId,
                cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Insere o intent de seleção e publica o envelope na MESMA transação
    /// (ADR 0006): falha de persistência do outbox desfaz o intent. Em uma
    /// transação ambiente (middleware Wolverine/caller), o commit e o flush
    /// pertencem ao dono da transação.
    /// </summary>
    private async Task<Guid?> EnqueueSelectionDeliveryAsync(
        Guid inboundMessageId,
        CancellationToken cancellationToken)
    {
        Guid? deliveryId = null;
        var ambientTransaction = database.Database.CurrentTransaction;
        await using var ownedTransaction = ambientTransaction is null
            ? await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var transaction = ambientTransaction ?? ownedTransaction!;

        var connection = database.Database.GetDbConnection();
        if (!connection.State.HasFlag(ConnectionState.Open))
        {
            await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText =
                "SELECT delivery_id FROM notifications.enqueue_inbound_telegram_selection_reply(@inbound_id)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "inbound_id";
            parameter.Value = inboundMessageId;
            command.Parameters.Add(parameter);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            deliveryId = result is Guid opaqueDeliveryId ? opaqueDeliveryId : null;

            if (deliveryId is { } pendingDeliveryId)
            {
                outbox.Enroll(database);
                await outbox.PublishAsync(
                    new NotificationDeliveryRequested(pendingDeliveryId),
                    new DeliveryOptions { CorrelationId = inboundMessageId.ToString("D") })
                    .ConfigureAwait(false);
            }
        }

        if (ownedTransaction is null)
        {
            return deliveryId;
        }

        await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (deliveryId is not null)
        {
            try
            {
                await outbox.FlushOutgoingMessagesAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Inbound Telegram receipt {Operation} {Module} {Result} {Reason}",
                    Operation,
                    Module,
                    "selection_delivery",
                    "outbox_flush_deferred");
            }
        }

        return deliveryId;
    }

    private sealed record TelegramInboundReceipt(string Status, bool Transitioned);
}
