namespace Vinx.Erp.ApiHost.Notifications;

/// <summary>
/// Resolve o ator (usuário, empresa, membership) de um endereço inbound do
/// WhatsApp. A resolução é fechada: nunca escolhe entre múltiplas
/// identidades e nunca retorna estado em aberto.
/// </summary>
public interface IInboundActorResolver
{
    Task<InboundActorResolution> ResolveAsync(
        InboundChannelAddress address,
        CancellationToken cancellationToken
    );
}
