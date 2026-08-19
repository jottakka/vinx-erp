namespace Vinx.Erp.Notifications;

/// <summary>
/// Resolve o provider do canal entre os registrados; canal sem provider é
/// erro permanente de configuração.
/// </summary>
public sealed class NotificationProviderResolver(IEnumerable<INotificationProvider> providers)
{
    public INotificationProvider Resolve(string channel)
    {
        var provider = providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Channel, channel, StringComparison.Ordinal)
        );
        return provider ?? throw new NotificationProviderMissingException(channel);
    }
}
