namespace Vinx.Erp.Foundation.Notifications;

/// <summary>
/// Chave de deduplicação de entrega: no máximo uma entrega por evento,
/// destinatário, canal e template. Espelha a constraint UNIQUE de
/// notifications.notification_intents.
/// </summary>
public readonly record struct NotificationDeliveryKey(
    string EventKey,
    Guid RecipientUserId,
    string Channel,
    string TemplateKey
);
