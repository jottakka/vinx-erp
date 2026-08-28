namespace Vinx.Erp.Notifications.Providers.Meta;

/// <summary>
/// Compatibilidade com envelopes de imagem já persistidos antes do adapter
/// comum. Toda autoridade de IA, materialização e resposta pertence ao adapter.
/// </summary>
public sealed class InboundWhatsAppInteractionService(InboundAgentChannelAdapter adapter)
{
    public Task ProcessAsync(Guid inboundMessageId, CancellationToken cancellationToken) =>
        adapter.ProcessAsync(
            new InboundAgentRequestReceived(inboundMessageId, AgentChannelKind.WhatsApp),
            cancellationToken);
}
