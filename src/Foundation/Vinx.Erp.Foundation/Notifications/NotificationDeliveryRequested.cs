namespace Vinx.Erp.Foundation.Notifications;

/// <summary>
/// Intenção de entrega enfileirada na transação do produtor e consumida pelo
/// worker de notificações; contém somente o identificador opaco da entrega.
/// </summary>
public sealed record NotificationDeliveryRequested(Guid DeliveryId);
