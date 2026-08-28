using Vinx.Erp.Notifications;

namespace Vinx.Erp.WorkerHost;

/// <summary>Consome o envelope opaco e delega toda leitura autorizada ao adapter.</summary>
[InboundWhatsAppFailurePolicy]
public sealed class InboundAgentRequestReceivedHandler(
    InboundAgentChannelAdapter adapter,
    ILogger<InboundAgentRequestReceivedHandler> logger)
{
    public async Task Handle(
        InboundAgentRequestReceived message,
        CancellationToken cancellationToken)
    {
        if (message.InboundMessageId == Guid.Empty
            || message.Channel != AgentChannelKind.WhatsApp)
        {
            throw new InboundWhatsAppDeadLetterException(
                InboundWhatsAppDeadLetterException.InvalidExceptionType,
                "Inbound agent request rejected the envelope.");
        }

        try
        {
            await adapter.ProcessAsync(message, cancellationToken);
        }
        catch (InboundWhatsAppPermanentException)
        {
            throw new InboundWhatsAppDeadLetterException(
                InboundWhatsAppDeadLetterException.InvalidExceptionType,
                "Inbound agent request rejected the envelope.");
        }

        logger.LogInformation(
            "Inbound agent request {Operation} {Module} {Result}",
            "inbound_agent_request",
            "notifications",
            "processed");
    }
}
