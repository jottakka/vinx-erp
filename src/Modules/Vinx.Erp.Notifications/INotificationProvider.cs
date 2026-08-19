namespace Vinx.Erp.Notifications;

/// <summary>
/// Contexto da tentativa de entrega: identificadores opacos suficientes para
/// o provider buscar, por fronteira estreita, somente os dados do seu canal.
/// </summary>
public sealed record NotificationDeliveryRequest(Guid DeliveryId, Guid AttemptId);

/// <summary>
/// Fronteira estreita de entrega por canal; a implementação é chamada pelo
/// worker fora de qualquer transação de banco aberta e não conhece o envelope.
/// </summary>
public interface INotificationProvider
{
    string Channel { get; }

    Task<NotificationProviderResult> DeliverAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken
    );
}
