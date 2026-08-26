namespace Vinx.Erp.ApiHost.Notifications;

/// <summary>
/// Resolve o ator de um chat privado Telegram pela fronteira SQL fechada.
/// </summary>
public interface IInboundTelegramActorResolver
{
    Task<InboundActorResolution> ResolveAsync(
        long chatId,
        CancellationToken cancellationToken
    );
}
