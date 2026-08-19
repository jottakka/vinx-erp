namespace Vinx.Erp.Notifications;

/// <summary>
/// Fronteira estreita de entrega por canal; a implementação é chamada pelo
/// worker fora de qualquer transação de banco aberta e não conhece o envelope.
/// </summary>
public interface INotificationProvider
{
    string Channel { get; }

    Task<NotificationProviderResult> DeliverAsync(
        Guid deliveryId,
        CancellationToken cancellationToken
    );
}
