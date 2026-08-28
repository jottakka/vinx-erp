using System.Text.Json;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Platform;
using Wolverine;
using Wolverine.Persistence.Durability;

namespace Vinx.Erp.WorkerHost;

internal sealed class WorkerDeadLetterRedactionInterceptor : IDeadLetterInterceptor
{
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web
    );

    public ValueTask<Exception?> BeforeStoreAsync(
        Envelope envelope,
        Exception? exception,
        CancellationToken cancellation
    )
    {
        RedactEnvelope(envelope);
        return ValueTask.FromResult<Exception?>(
            new WorkerOwnerInvitationDeadLetterException(
                WorkerOwnerInvitationDeadLetterException.UnexpectedExceptionType,
                "Worker delivery ingress failed unexpectedly."
            )
        );
    }

    /// <summary>
    /// Redige por tipo de mensagem: o corpo vira o registro vazio do próprio
    /// tipo e os headers diagnósticos são descartados antes da DLQ.
    /// </summary>
    internal void RedactEnvelope(Envelope envelope)
    {
        switch (envelope.Message)
        {
            case NotificationDeliveryRequested:
                var redactedNotification = new NotificationDeliveryRequested(Guid.Empty);
                envelope.Message = redactedNotification;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedNotification,
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
            default:
                var redactedMessage = new OwnerInvitationDeliveryRequested(Guid.Empty);
                envelope.Message = redactedMessage;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedMessage,
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
        }
    }
}
