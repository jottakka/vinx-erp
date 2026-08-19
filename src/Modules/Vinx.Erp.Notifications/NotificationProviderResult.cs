namespace Vinx.Erp.Notifications;

public enum NotificationProviderResultKind
{
    /// <summary>Provider confirmou a entrega; finaliza como entregue.</summary>
    Delivered,

    /// <summary>Provider rejeitou definitivamente; finaliza sem retry.</summary>
    Rejected,

    /// <summary>Timeout/resultado incerto; finaliza ambíguo sem retry cego.</summary>
    Ambiguous,
}

/// <summary>
/// Resultado seguro do provider: somente classificação estável, sem corpo,
/// payload ou identificador externo controlado por terceiros.
/// </summary>
public sealed record NotificationProviderResult(NotificationProviderResultKind Kind);
