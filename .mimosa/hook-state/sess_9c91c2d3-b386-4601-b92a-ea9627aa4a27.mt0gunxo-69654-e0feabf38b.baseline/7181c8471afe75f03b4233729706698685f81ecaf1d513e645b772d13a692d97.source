namespace Vinx.Erp.Notifications;

/// <summary>
/// Entrega inválida para o worker (identificador inexistente, envelope
/// rejeitado ou violação de estado): permanente, sem retry e sem chamada
/// externa adicional.
/// </summary>
public sealed class NotificationDeliveryPermanentException()
    : Exception("Notification delivery was rejected by the worker boundary.");
