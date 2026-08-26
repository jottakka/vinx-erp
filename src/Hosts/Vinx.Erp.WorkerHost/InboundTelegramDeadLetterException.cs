using Wolverine.Persistence.Durability;

namespace Vinx.Erp.WorkerHost;

/// <summary>Marco estável e redigido para a DLQ do recibo inbound Telegram.</summary>
public sealed class InboundTelegramDeadLetterException(string exceptionType, string message)
    : Exception(message),
        IDeadLetterExceptionInfo
{
    public const string InvalidExceptionType = "telegram_inbound_invalid";
    public const string RetryExhaustedExceptionType = "telegram_inbound_retry_exhausted";
    public const string UnexpectedExceptionType = "telegram_inbound_unexpected";

    public string ExceptionType { get; } = exceptionType;

    public string ExceptionMessage { get; } = message;
}
