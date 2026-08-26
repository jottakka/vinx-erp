using System.Text.Json;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Platform;
using Vinx.Erp.Notifications;
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
        if (exception is InboundTelegramDeadLetterException telegramException)
        {
            return ValueTask.FromResult<Exception?>(RedactTelegramException(telegramException));
        }

        return ValueTask.FromResult<Exception?>(
            new WorkerOwnerInvitationDeadLetterException(
                WorkerOwnerInvitationDeadLetterException.UnexpectedExceptionType,
                "Worker delivery ingress failed unexpectedly."
            )
        );
    }

    private static InboundTelegramDeadLetterException RedactTelegramException(
        InboundTelegramDeadLetterException exception
    ) =>
        exception.ExceptionType switch
        {
            InboundTelegramDeadLetterException.InvalidExceptionType => new(
                InboundTelegramDeadLetterException.InvalidExceptionType,
                "Inbound Telegram receipt rejected the envelope."
            ),
            InboundTelegramDeadLetterException.RetryExhaustedExceptionType => new(
                InboundTelegramDeadLetterException.RetryExhaustedExceptionType,
                "Inbound Telegram receipt exhausted transient retries."
            ),
            InboundTelegramDeadLetterException.UnexpectedExceptionType => new(
                InboundTelegramDeadLetterException.UnexpectedExceptionType,
                "Inbound Telegram receipt failed unexpectedly."
            ),
            _ => new(
                InboundTelegramDeadLetterException.UnexpectedExceptionType,
                "Inbound Telegram receipt failed unexpectedly."
            ),
        };

    /// <summary>
    /// Redige por tipo de mensagem: o corpo vira o registro vazio do próprio
    /// tipo e os headers diagnósticos são descartados antes da DLQ.
    /// </summary>
    internal static void RedactEnvelope(Envelope envelope)
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
            case InboundWhatsAppImageReceived:
                var redactedInbound = new InboundWhatsAppImageReceived(Guid.Empty);
                envelope.Message = redactedInbound;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedInbound,
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
            case InboundWhatsAppTextCorrelationExpired:
                var redactedExpiry = new InboundWhatsAppTextCorrelationExpired(Guid.Empty);
                envelope.Message = redactedExpiry;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedExpiry,
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
            case InboundWhatsAppDocumentReceived:
                var redactedDocument = new InboundWhatsAppDocumentReceived(Guid.Empty);
                envelope.Message = redactedDocument;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedDocument,
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
            case InboundTelegramTextReceived:
                var redactedTelegram = new InboundTelegramTextReceived(Guid.Empty);
                envelope.Message = redactedTelegram;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedTelegram,
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
