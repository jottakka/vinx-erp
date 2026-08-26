using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
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
    ILogger<InboundTelegramTextReceivedHandler> logger
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

    private sealed record TelegramInboundReceipt(string Status, bool Transitioned);
}
