using JasperFx.CodeGeneration;
using Npgsql;
using Vinx.Erp.Notifications;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Vinx.Erp.WorkerHost;

/// <summary>Retry finito para transitivos; descarte redigido e tipado para o resto.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class NotificationDeliveryFailurePolicyAttribute : ModifyHandlerChainAttribute
{
    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain
            .OnException<NotificationDeliveryDeadLetterException>()
            .CustomActionIndefinitely(
                NotificationDeliveryRequestedHandler.PersistInvalidDeadLetterAsync,
                "Redact and dead-letter invalid notification delivery"
            );
        chain
            .OnException<NpgsqlException>(exception => exception.IsTransient, "transient_npgsql")
            .ScheduleRetry(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
            .Then.CustomActionIndefinitely(
                NotificationDeliveryRequestedHandler.PersistRetryExhaustedDeadLetterAsync,
                "Redact and dead-letter exhausted notification delivery"
            );
        chain
            .OnException<NotificationProviderTransientException>()
            .ScheduleRetry(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
            .Then.CustomActionIndefinitely(
                NotificationDeliveryRequestedHandler.PersistRetryExhaustedDeadLetterAsync,
                "Redact and dead-letter exhausted notification delivery"
            );
        chain
            .OnAnyException()
            .CustomActionIndefinitely(
                NotificationDeliveryRequestedHandler.PersistUnexpectedDeadLetterAsync,
                "Redact and dead-letter unexpected notification delivery"
            );
    }
}
