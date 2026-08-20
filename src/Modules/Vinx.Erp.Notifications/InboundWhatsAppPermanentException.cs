namespace Vinx.Erp.Notifications;

/// <summary>
/// Falha permanente e determinística do processamento inbound (validação,
/// rejeição do provider, OCR sem texto): sem retry, com motivo estável.
/// </summary>
public sealed class InboundWhatsAppPermanentException(string message)
    : Exception(message);
