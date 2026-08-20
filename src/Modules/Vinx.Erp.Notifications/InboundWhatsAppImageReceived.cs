namespace Vinx.Erp.Notifications;

/// <summary>
/// Evento de entrada deduplicado: uma mensagem de imagem recebida no webhook
/// do WhatsApp aguardando processamento (OCR + turno único de IA + resposta).
/// </summary>
public sealed record InboundWhatsAppImageReceived(Guid InboundMessageId);
