using Wolverine.Persistence.Durability;

namespace Vinx.Erp.WorkerHost;

/// <summary>
/// Classifica uma mensagem de convite que nunca poderá ser processada sem persistir detalhes internos.
/// </summary>
internal sealed class WorkerOwnerInvitationDeliveryPermanentException
    : Exception,
        IDeadLetterExceptionInfo
{
    public string ExceptionType => "owner_invitation_delivery_failed";

    public string ExceptionMessage => "Worker delivery ingress failed.";
}
