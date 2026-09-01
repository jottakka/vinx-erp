namespace Vinx.Erp.Foundation.Notifications;

/// <summary>
/// Nomes canônicos das filas Wolverine de entrega cujos contratos de mensagem
/// pertencem à Foundation (convites e entrega de notificação). Fonte única
/// compartilhada pelos hosts: o ApiHost publica pelo outbox transacional e o
/// WorkerHost consome com inbox durável (e re-publica entregas de notificação
/// após o processamento do provider).
/// </summary>
public static class DeliveryQueues
{
    /// <summary>Fila durável da entrega do convite do owner.</summary>
    public const string OwnerInvitation = "owner-invitation-delivery";

    /// <summary>Fila durável da entrega do convite do membro.</summary>
    public const string MemberInvitation = "member-invitation-delivery";

    /// <summary>Fila durável da entrega de notificações por canal.</summary>
    public const string Notification = "notification-delivery";
}
