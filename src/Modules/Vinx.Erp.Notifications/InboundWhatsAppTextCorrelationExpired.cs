namespace Vinx.Erp.Notifications;

/// <summary>Prazo de correlação do texto inbound atingido.</summary>
public sealed record InboundWhatsAppTextCorrelationExpired(Guid InboundMessageId);
