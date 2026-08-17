using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Platform;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Vinx.Erp.WorkerHost;

[OwnerInvitationDeliveryFailurePolicy]
public sealed class OwnerInvitationDeliveryRequestedHandler(
    OwnerInvitationDeliveryIngressService ingress,
    ILogger<OwnerInvitationDeliveryRequestedHandler> logger
)
{
    private const string Operation = "owner_invitation_delivery_ingress";
    private const string Module = "foundation";

    [Transactional(typeof(FoundationDbContext))]
    public async Task Handle(
        OwnerInvitationDeliveryRequested message,
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
            throw new WorkerOwnerInvitationDeliveryPermanentException();
        }

        OwnerInvitationDeliveryIngressResult result;
        try
        {
            result = await ingress.AcceptAsync(
                message.InvitationId,
                correlationId,
                cancellationToken
            );
        }
        catch (OwnerInvitationIngressPermanentException)
        {
            throw new WorkerOwnerInvitationDeliveryPermanentException();
        }

        logger.LogInformation(
            "Worker ingress {Operation} {Module} {Result}",
            Operation,
            Module,
            result.Transitioned ? "transitioned" : "replay"
        );
    }

    internal static async ValueTask PersistRedactedWorkerDeadLetterAsync(
        IWolverineRuntime runtime,
        IEnvelopeLifecycle lifecycle,
        Exception exception
    )
    {
        var envelope =
            lifecycle.Envelope
            ?? throw new InvalidOperationException("Worker dead-letter lifecycle has no envelope.");

        // PostgreSQL's durable receiver stamps diagnostic headers after the normal interceptor
        // hook. This Worker has one message store, so persist directly only after redaction.
        await runtime
            .Services.GetRequiredService<WorkerDeadLetterRedactionInterceptor>()
            .BeforeStoreAsync(envelope, exception, runtime.Cancellation);
        await runtime.Storage.Inbox.MoveToDeadLetterStorageAsync(
            envelope,
            new WorkerOwnerInvitationDeliveryPermanentException()
        );
        await lifecycle.CompleteAsync();
    }
}

/// <summary>Restringe o descarte seguro ao único handler de entrega de convite.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class OwnerInvitationDeliveryFailurePolicyAttribute : ModifyHandlerChainAttribute
{
    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain
            .OnException<WorkerOwnerInvitationDeliveryPermanentException>()
            .CustomActionIndefinitely(
                OwnerInvitationDeliveryRequestedHandler.PersistRedactedWorkerDeadLetterAsync,
                "Redact and dead-letter invalid owner invitation delivery"
            );
        chain
            .OnException<NpgsqlException>(exception => exception.IsTransient, "transient_npgsql")
            .ScheduleRetry(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(30)
            )
            .Then.CustomActionIndefinitely(
                OwnerInvitationDeliveryRequestedHandler.PersistRedactedWorkerDeadLetterAsync,
                "Redact and dead-letter exhausted owner invitation delivery"
            );
        chain
            .OnAnyException()
            .CustomActionIndefinitely(
                OwnerInvitationDeliveryRequestedHandler.PersistRedactedWorkerDeadLetterAsync,
                "Redact and dead-letter unexpected owner invitation delivery"
            );
    }
}
