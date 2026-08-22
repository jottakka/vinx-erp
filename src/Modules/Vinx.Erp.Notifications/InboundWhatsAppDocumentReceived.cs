namespace Vinx.Erp.Notifications;

/// <summary>
/// Documento inbound admitido, pronto para a fatia de quarentena (#272 PR2):
/// somente o identificador opaco da admissão; nenhum conteúdo no envelope.
/// </summary>
public sealed record InboundWhatsAppDocumentReceived(Guid InboundMessageId);
