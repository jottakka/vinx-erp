using JasperFx.CodeGeneration;
using Npgsql;
using Vinx.Erp.Notifications;
using Vinx.Erp.Notifications.Providers.Meta;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Vinx.Erp.WorkerHost;

[InboundWhatsAppFailurePolicy]
public sealed class InboundWhatsAppImageReceivedHandler(
    InboundWhatsAppInteractionService interaction,
    ILogger<InboundWhatsAppImageReceivedHandler> logger
)
{
    public async Task Handle(
        InboundWhatsAppImageReceived message,
        CancellationToken cancellationToken
    )
    {
        if (message.InboundMessageId == Guid.Empty)
        {
            throw new InboundWhatsAppDeadLetterException(
                InboundWhatsAppDeadLetterException.InvalidExceptionType,
                "Inbound WhatsApp interaction rejected the envelope."
            );
        }

        try
        {
            await interaction.ProcessAsync(message.InboundMessageId, cancellationToken);
        }
        catch (InboundWhatsAppPermanentException)
        {
            throw new InboundWhatsAppDeadLetterException(
                InboundWhatsAppDeadLetterException.InvalidExceptionType,
                "Inbound WhatsApp interaction rejected the envelope."
            );
        }

#pragma warning disable CA1873 // Os argumentos são constantes ou valores baratos; o guard de logging não agrega ganho.
        logger.LogInformation(
            "Inbound WhatsApp interaction {Operation} {Module} {Result}",
            "whatsapp_inbound_interaction",
            "notifications",
            "processed"
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
        await runtime.Storage.Inbox.MoveToDeadLetterStorageAsync(
            envelope,
            new InboundWhatsAppDeadLetterException(exceptionType, exceptionMessage)
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
            InboundWhatsAppDeadLetterException.InvalidExceptionType,
            "Inbound WhatsApp interaction rejected the envelope."
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
            InboundWhatsAppDeadLetterException.RetryExhaustedExceptionType,
            "Inbound WhatsApp interaction exhausted transient retries."
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
            InboundWhatsAppDeadLetterException.UnexpectedExceptionType,
            "Inbound WhatsApp interaction failed unexpectedly."
        );
}

/// <summary>Retry finito para transitivos; descarte redigido e tipado para o resto.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class InboundWhatsAppFailurePolicyAttribute : ModifyHandlerChainAttribute
{
    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain
            .OnException<InboundWhatsAppDeadLetterException>()
            .CustomActionIndefinitely(
                InboundWhatsAppImageReceivedHandler.PersistInvalidDeadLetterAsync,
                "Redact and dead-letter invalid inbound WhatsApp interaction"
            );
        chain
            .OnException<NpgsqlException>(exception => exception.IsTransient, "transient_npgsql")
            .ScheduleRetry(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
            .Then.CustomActionIndefinitely(
                InboundWhatsAppImageReceivedHandler.PersistRetryExhaustedDeadLetterAsync,
                "Redact and dead-letter exhausted inbound WhatsApp interaction"
            );
        chain
            .OnException<NotificationProviderTransientException>()
            .ScheduleRetry(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
            .Then.CustomActionIndefinitely(
                InboundWhatsAppImageReceivedHandler.PersistRetryExhaustedDeadLetterAsync,
                "Redact and dead-letter exhausted inbound WhatsApp interaction"
            );
        chain
            .OnAnyException()
            .CustomActionIndefinitely(
                InboundWhatsAppImageReceivedHandler.PersistUnexpectedDeadLetterAsync,
                "Redact and dead-letter unexpected inbound WhatsApp interaction"
            );
    }
}
