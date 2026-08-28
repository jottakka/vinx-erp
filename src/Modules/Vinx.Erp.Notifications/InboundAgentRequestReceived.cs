namespace Vinx.Erp.Notifications;

/// <summary>Canais fechados que podem encaminhar uma admissão ao núcleo compartilhado.</summary>
public enum AgentChannelKind
{
    Web,
    WhatsApp,
    Telegram,
}

/// <summary>
/// Envelope opaco da fila: não carrega endereço, conteúdo, arquivo ou dado do provider.
/// </summary>
public sealed record InboundAgentRequestReceived(
    Guid InboundMessageId,
    AgentChannelKind Channel);
