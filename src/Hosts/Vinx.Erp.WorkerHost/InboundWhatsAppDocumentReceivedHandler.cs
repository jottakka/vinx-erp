using Vinx.Erp.Notifications;
using Vinx.Erp.Notifications.Providers.Meta;
using Wolverine;

namespace Vinx.Erp.WorkerHost;

/// <summary>
/// Consuma a fila whatsapp-inbound-documents: quarentena do documento
/// admitido (#272 PR2) com a mesma política de retry/DLQ redigida do inbound.
/// </summary>
[InboundWhatsAppFailurePolicy]
public sealed class InboundWhatsAppDocumentReceivedHandler(
    InboundWhatsAppDocumentService documentService,
    ILogger<InboundWhatsAppDocumentReceivedHandler> logger
)
{
    public async Task Handle(
        InboundWhatsAppDocumentReceived message,
        CancellationToken cancellationToken
    )
    {
        if (message.InboundMessageId == Guid.Empty)
        {
            throw new InboundWhatsAppDeadLetterException(
                InboundWhatsAppDeadLetterException.InvalidExceptionType,
                "Inbound WhatsApp document rejected the envelope."
            );
        }

        try
        {
            await documentService.ProcessAsync(message.InboundMessageId, cancellationToken);
        }
        catch (InboundWhatsAppPermanentException)
        {
            throw new InboundWhatsAppDeadLetterException(
                InboundWhatsAppDeadLetterException.InvalidExceptionType,
                "Inbound WhatsApp document rejected the envelope."
            );
        }

        logger.LogInformation(
            "Inbound WhatsApp document {Operation} {Module} {Result}",
            "whatsapp_inbound_document",
            "notifications",
            "processed"
        );
    }
}
