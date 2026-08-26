using Wolverine.Persistence.Durability;

namespace Vinx.Erp.WorkerHost;

/// <summary>
/// Classifica o descarte terminal de uma entrega de convite de membro por motivo, sem persistir detalhes internos.
/// </summary>
internal sealed class WorkerMemberInvitationDeadLetterException(
    string exceptionType,
    string exceptionMessage
)
    : Exception,
        IDeadLetterExceptionInfo
{
    public const string InvalidExceptionType = "member_invitation_delivery_invalid";
    public const string RetryExhaustedExceptionType =
        "member_invitation_delivery_retry_exhausted";
    public const string UnexpectedExceptionType = "member_invitation_delivery_unexpected";

    public string ExceptionType { get; } = exceptionType;

    public string ExceptionMessage { get; } = exceptionMessage;
}
