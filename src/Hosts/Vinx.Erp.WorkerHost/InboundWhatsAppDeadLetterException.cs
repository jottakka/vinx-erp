namespace Vinx.Erp.WorkerHost;

/// <summary>
/// Marco de descarte do inbound do WhatsApp com tipo de erro estável para a
/// DLQ; nunca carrega payload, legenda ou identificador do remetente.
/// </summary>
public sealed class InboundWhatsAppDeadLetterException(string exceptionType, string message)
    : Exception(message)
{
    public const string InvalidExceptionType = "whatsapp_inbound_invalid";
    public const string RetryExhaustedExceptionType = "whatsapp_inbound_retry_exhausted";
    public const string UnexpectedExceptionType = "whatsapp_inbound_unexpected";

    public string ExceptionType { get; } = exceptionType;
}
