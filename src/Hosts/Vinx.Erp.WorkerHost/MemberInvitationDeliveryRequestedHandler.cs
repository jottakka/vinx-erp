using JasperFx.CodeGeneration;
using Npgsql;
using Vinx.Erp.Foundation.Permissions;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Vinx.Erp.WorkerHost;

[MemberInvitationDeliveryFailurePolicy]
public sealed class MemberInvitationDeliveryRequestedHandler(
    MemberInvitationProviderDeliveryService delivery,
    ILogger<MemberInvitationDeliveryRequestedHandler> logger
)
{
    private const string Operation = "member_invitation_provider_delivery";
    private const string Module = "foundation";

    public async Task Handle(
        MemberInvitationDeliveryRequested message,
        Envelope envelope,
        CancellationToken cancellationToken
    )
    {
        if (
            message.InvitationId == Guid.Empty
            || !Guid.TryParse(envelope.CorrelationId, out var correlationId)
            || correlationId == Guid.Empty
        )
        {
            throw new WorkerMemberInvitationDeadLetterException(
                WorkerMemberInvitationDeadLetterException.InvalidExceptionType,
                "Worker delivery ingress rejected the envelope."
            );
        }

        MemberInvitationProviderDeliveryResult result;
        try
        {
            result = await delivery.DeliverAsync(
                message.InvitationId,
                correlationId,
                cancellationToken
            );
        }
        catch (MemberInvitationIngressPermanentException)
        {
            throw new WorkerMemberInvitationDeadLetterException(
                WorkerMemberInvitationDeadLetterException.InvalidExceptionType,
                "Worker delivery ingress rejected the envelope."
            );
        }

#pragma warning disable CA1873 // Os argumentos são constantes ou valores baratos; o guard de logging não agrega ganho.
        logger.LogInformation(
            "Worker provider delivery {Operation} {Module} {Result}",
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

        // PostgreSQL's durable receiver stamps diagnostic headers after the normal interceptor
        // hook. This Worker has one message store, so persist directly only after redaction.
        WorkerDeadLetterRedactionInterceptor.RedactEnvelope(envelope);
        await runtime.Storage.Inbox.MoveToDeadLetterStorageAsync(
            envelope,
            new WorkerMemberInvitationDeadLetterException(exceptionType, exceptionMessage)
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
            WorkerMemberInvitationDeadLetterException.InvalidExceptionType,
            "Worker delivery ingress rejected the envelope."
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
            WorkerMemberInvitationDeadLetterException.RetryExhaustedExceptionType,
            "Worker delivery ingress exhausted transient retries."
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
            WorkerMemberInvitationDeadLetterException.UnexpectedExceptionType,
            "Worker delivery ingress failed unexpectedly."
        );
}

/// <summary>Restringe o descarte seguro ao único handler de entrega de convite de membro.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class MemberInvitationDeliveryFailurePolicyAttribute : ModifyHandlerChainAttribute
{
    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain
            .OnException<WorkerMemberInvitationDeadLetterException>()
            .CustomActionIndefinitely(
                MemberInvitationDeliveryRequestedHandler.PersistInvalidDeadLetterAsync,
                "Redact and dead-letter invalid member invitation delivery"
            );
        chain
            .OnException<NpgsqlException>(exception => exception.IsTransient, "transient_npgsql")
            .ScheduleRetry(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(30)
            )
            .Then.CustomActionIndefinitely(
                MemberInvitationDeliveryRequestedHandler.PersistRetryExhaustedDeadLetterAsync,
                "Redact and dead-letter exhausted member invitation delivery"
            );
        chain
            .OnAnyException()
            .CustomActionIndefinitely(
                MemberInvitationDeliveryRequestedHandler.PersistUnexpectedDeadLetterAsync,
                "Redact and dead-letter unexpected member invitation delivery"
            );
    }
}
