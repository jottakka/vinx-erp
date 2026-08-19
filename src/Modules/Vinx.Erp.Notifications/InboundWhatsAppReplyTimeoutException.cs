namespace Vinx.Erp.Notifications;

/// <summary>
/// Timeout do envio da resposta inbound: resultado incerto no provider, sem
/// retry cego (espelha o ambíguo terminal do envio do W3).
/// </summary>
public sealed class InboundWhatsAppReplyTimeoutException()
    : Exception("Meta WhatsApp reply timed out.");
