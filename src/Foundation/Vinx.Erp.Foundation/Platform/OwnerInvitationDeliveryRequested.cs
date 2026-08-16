namespace Vinx.Erp.Foundation.Platform;

/// <summary>
/// Solicita a entrega futura do convite já reservado para o dono inicial.
/// </summary>
public sealed record OwnerInvitationDeliveryRequested(Guid InvitationId);
