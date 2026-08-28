namespace Vinx.Erp.Notifications.Providers.Meta;

/// <summary>
/// Compatibilidade com envelopes de documento já persistidos antes do adapter
/// comum. O caminho novo publica somente InboundAgentRequestReceived.
/// </summary>
public sealed class InboundWhatsAppDocumentService(InboundAgentChannelAdapter adapter)
{
    public Task ProcessAsync(Guid inboundMessageId, CancellationToken cancellationToken) =>
        adapter.ProcessAsync(
            new InboundAgentRequestReceived(inboundMessageId, AgentChannelKind.WhatsApp),
            cancellationToken);
}
