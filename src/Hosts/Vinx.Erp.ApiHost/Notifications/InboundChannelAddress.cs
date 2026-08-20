namespace Vinx.Erp.ApiHost.Notifications;

/// <summary>
/// Endereço estável do canal inbound do WhatsApp: o número de negócio que
/// recebeu a mensagem (<c>phone_number_id</c>) e o remetente (<c>wa_id</c>,
/// dígitos sem o sinal de <c>+</c>).
/// </summary>
public sealed record InboundChannelAddress(string PhoneNumberId, string WaId);
