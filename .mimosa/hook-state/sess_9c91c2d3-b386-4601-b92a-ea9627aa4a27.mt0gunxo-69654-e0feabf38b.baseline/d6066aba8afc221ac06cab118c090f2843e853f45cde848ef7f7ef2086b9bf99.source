namespace Vinx.Erp.Notifications;

/// <summary>
/// Canal sem provider registrado: erro de configuração permanente, sem
/// chamada externa e sem retry.
/// </summary>
public sealed class NotificationProviderMissingException(string channel)
    : Exception($"No notification provider is registered for channel '{channel}'.");
