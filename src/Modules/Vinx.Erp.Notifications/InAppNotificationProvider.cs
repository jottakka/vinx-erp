using Vinx.Erp.Foundation.Notifications;

namespace Vinx.Erp.Notifications;

/// <summary>
/// O alerta in-app é a própria linha de intenção persistida pelo produtor;
/// entregar in-app é confirmar a entrega sem qualquer efeito externo. A
/// leitura do inbox permanece da #56.
/// </summary>
public sealed class InAppNotificationProvider : INotificationProvider
{
    public string Channel => NotificationChannel.InApp;

    public Task<NotificationProviderResult> DeliverAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult(
            new NotificationProviderResult(NotificationProviderResultKind.Delivered)
        );
}
