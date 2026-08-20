using Wolverine.Persistence.Durability;

namespace Vinx.Erp.WorkerHost;

/// <summary>
/// Classifica o descarte terminal de uma entrega de convite por motivo, sem persistir detalhes internos.
/// </summary>
internal sealed class WorkerOwnerInvitationDeadLetterException(
    string exceptionType,
    string exceptionMessage
    )
        : Exception,
        IDeadLetterExceptionInfo
{
    public const string InvalidExceptionType = "owner_invitation_delivery_invalid";
    public const string RetryExhaustedExceptionType =
        "owner_invitation_delivery_retry_exhausted";
    public const string UnexpectedExceptionType = "owner_invitation_delivery_unexpected";

    public string ExceptionType { get; } = exceptionType;

    public string ExceptionMessage { get; } = exceptionMessage;
}
