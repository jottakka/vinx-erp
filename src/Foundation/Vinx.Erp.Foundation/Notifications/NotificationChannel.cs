namespace Vinx.Erp.Foundation.Notifications;

/// <summary>
/// Canais de notificação suportados; o canal in-app é obrigatório e não é
/// controlável por preferência do destinatário.
/// </summary>
public static class NotificationChannel
{
    public const string InApp = "in_app";
    public const string WhatsApp = "whatsapp";
}
