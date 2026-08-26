using JasperFx.CodeGeneration;
using Npgsql;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Vinx.Erp.WorkerHost;

/// <summary>Retry finito do banco; descarte redigido e tipado para o recibo Telegram.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class InboundTelegramFailurePolicyAttribute : ModifyHandlerChainAttribute
{
    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain
            .OnException<InboundTelegramDeadLetterException>()
            .CustomActionIndefinitely(
                InboundTelegramTextReceivedHandler.PersistInvalidDeadLetterAsync,
                "Redact and dead-letter invalid inbound Telegram receipt"
            );
        chain
            .OnException<NpgsqlException>(exception => exception.IsTransient, "transient_npgsql")
            .ScheduleRetry(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
            .Then.CustomActionIndefinitely(
                InboundTelegramTextReceivedHandler.PersistRetryExhaustedDeadLetterAsync,
                "Redact and dead-letter exhausted inbound Telegram receipt"
            );
        chain
            .OnAnyException()
            .CustomActionIndefinitely(
                InboundTelegramTextReceivedHandler.PersistUnexpectedDeadLetterAsync,
                "Redact and dead-letter unexpected inbound Telegram receipt"
            );
    }
}
