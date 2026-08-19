namespace Vinx.Erp.WorkerHost;

/// <summary>
/// Marco de descarte da entrega de notificação com tipo de erro estável para
/// a DLQ; nunca carrega payload, headers ou identificador do destinatário.
/// </summary>
public sealed class NotificationDeliveryDeadLetterException(string exceptionType, string message)
    : Exception(message)
{
    public const string InvalidExceptionType = "notification_delivery_invalid";
    public const string RetryExhaustedExceptionType = "notification_delivery_retry_exhausted";
    public const string UnexpectedExceptionType = "notification_delivery_unexpected";
    public const string ProviderMissingExceptionType = "notification_delivery_provider_missing";

    public string ExceptionType { get; } = exceptionType;
}
