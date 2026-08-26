namespace Vinx.Erp.Notifications;

/// <summary>
/// Texto Telegram admitido para processamento posterior; contém somente a
/// identidade opaca da evidência durável.
/// </summary>
public sealed record InboundTelegramTextReceived(Guid InboundMessageId);
