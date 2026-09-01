namespace Vinx.Erp.Notifications;

/// <summary>
/// Nomes canônicos das filas Wolverine de propriedade do módulo Notifications.
/// Fonte única compartilhada pelos hosts: o ApiHost publica os eventos inbound
/// admitidos pelo webhook e o WorkerHost consome com inbox durável.
/// </summary>
public static class InboundQueues
{
    /// <summary>Fila durável de imagens, textos com correlação e requisições do agente.</summary>
    public const string WhatsAppInbound = "whatsapp-inbound";

    /// <summary>Fila durável dedicada aos documentos inbound (quarentena + OCR).</summary>
    public const string WhatsAppInboundDocuments = "whatsapp-inbound-documents";

    /// <summary>Fila durável dos textos inbound do Telegram.</summary>
    public const string TelegramInbound = "telegram-inbound";
}
