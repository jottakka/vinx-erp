using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Notifications;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Vinx.Erp.WorkerHost;

[NotificationDeliveryFailurePolicy]
public sealed class NotificationDeliveryRequestedHandler(
    NotificationDeliveryWorkerService delivery,
    ILogger<NotificationDeliveryRequestedHandler> logger
)
{
    private const string Operation = "notification_provider_delivery";
    private const string Module = "notifications";

    public async Task Handle(
        NotificationDeliveryRequested message,
        Envelope envelope,
        CancellationToken cancellationToken
    )
    {
        if (
            message.DeliveryId == Guid.Empty
            || !Guid.TryParse(envelope.CorrelationId, out var correlationId)
            || correlationId == Guid.Empty
        )
        {
            throw new NotificationDeliveryDeadLetterException(
                NotificationDeliveryDeadLetterException.InvalidExceptionType,
                "Notification delivery rejected the envelope."
            );
        }

        NotificationDeliveryResult result;
        try
        {
            result = await delivery.DeliverAsync(message.DeliveryId, correlationId, cancellationToken);
        }
        catch (NotificationProviderMissingException exception)
        {
            logger.LogWarning(
                exception,
                "Notification delivery {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "dead_letter",
                "provider_missing"
            );
            throw new NotificationDeliveryDeadLetterException(
                NotificationDeliveryDeadLetterException.ProviderMissingExceptionType,
                "Notification channel has no registered provider."
            );
        }
        catch (NotificationDeliveryPermanentException)
        {
            throw new NotificationDeliveryDeadLetterException(
                NotificationDeliveryDeadLetterException.InvalidExceptionType,
                "Notification delivery rejected the envelope."
            );
        }

#pragma warning disable CA1873 // Os argumentos são constantes ou valores baratos; o guard de logging não agrega ganho.
        logger.LogInformation(
            "Notification delivery {Operation} {Module} {Result}",
            Operation,
            Module,
            result.Finalized ? result.Status : "replay"
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

        // Persist only after redaction; the diagnostic headers stamped by the
        // durable receiver are dropped with the envelope rewrite.
        WorkerDeadLetterRedactionInterceptor.RedactEnvelope(envelope);
        runtime
            .Services.GetRequiredService<NotificationDeliveryMetrics>()
            .RecordDeadLetter(exceptionType);
        await runtime.Storage.Inbox.MoveToDeadLetterStorageAsync(
            envelope,
            new NotificationDeliveryDeadLetterException(exceptionType, exceptionMessage)
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
            NotificationDeliveryDeadLetterException.InvalidExceptionType,
            "Notification delivery rejected the envelope."
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
            NotificationDeliveryDeadLetterException.RetryExhaustedExceptionType,
            "Notification delivery exhausted transient retries."
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
            NotificationDeliveryDeadLetterException.UnexpectedExceptionType,
            "Notification delivery failed unexpectedly."
        );
}
