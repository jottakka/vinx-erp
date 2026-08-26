namespace Vinx.Erp.Foundation.Permissions;

/// <summary>
/// Solicita a entrega futura do convite já reservado para o membro convidado.
/// </summary>
public sealed record MemberInvitationDeliveryRequested(Guid InvitationId);
