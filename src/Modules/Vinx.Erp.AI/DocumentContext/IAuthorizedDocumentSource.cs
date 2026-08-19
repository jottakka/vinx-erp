namespace Vinx.Erp.AI.DocumentContext;

/// <summary>
/// Documento autorizado para leitura/OCR: stream limitado e seekable aberto
/// pela fronteira da Foundation somente após RLS, membership ativa,
/// capability dedicada de leitura/OCR, estado liberado, retenção, soft-delete
/// e checksum — sem URL assinada. O conteúdo é dado não confiável.
/// </summary>
public sealed record AuthorizedDocument(
    Guid FileRecordId,
    Guid CompanyId,
    string MediaType,
    string Classification,
    Stream Content);

/// <summary>
/// Fronteira de leitura autorizada de documentos. A implementação real é
/// owner da Foundation (fatia #134-A): sem ela, OCR em runtime é fail-closed.
/// Testes usam fontes sintéticas `Clean` controladas.
/// </summary>
public interface IAuthorizedDocumentSource
{
    Task<AuthorizedDocument> OpenReadAsync(Guid fileRecordId, CancellationToken cancellationToken);
}
