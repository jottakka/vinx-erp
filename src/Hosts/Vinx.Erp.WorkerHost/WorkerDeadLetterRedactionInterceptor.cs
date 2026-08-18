using System.Text.Json;
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

    internal void RedactEnvelope(Envelope envelope)
    {
        var redactedMessage = new OwnerInvitationDeliveryRequested(Guid.Empty);
        envelope.Message = redactedMessage;
        envelope.Data = JsonSerializer.SerializeToUtf8Bytes(redactedMessage, SerializerOptions);
        envelope.Headers.Clear();
    }
}
