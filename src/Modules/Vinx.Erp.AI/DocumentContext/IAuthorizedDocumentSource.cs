using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.AI.DocumentContext;

/// <summary>
/// Documento autorizado para leitura/OCR: stream limitado e seekable aberto
/// pela fronteira somente após RLS, membership ativa, capability dedicada de
/// leitura/OCR, estado liberado com veredito durável, retenção, tamanho
/// dentro da política e checksum — sem URL assinada. O conteúdo é dado não
/// confiável.
/// </summary>
public sealed record AuthorizedDocument(
    Guid FileRecordId,
    Guid CompanyId,
    string MediaType,
    string Classification,
    Stream Content);

/// <summary>
/// Fronteira de leitura autorizada de documentos. Ator e empresa são
/// EXPLÍCITOS e confiam no chamador autenticado (nunca no request do
/// cliente final). Implementação real: <see cref="FoundationAuthorizedDocumentSource"/>
/// sobre FileRecord/Foundation; sem ela, OCR em runtime é fail-closed.
/// </summary>
public interface IAuthorizedDocumentSource
{
    Task<AuthorizedDocument> OpenReadAsync(
        ActorContext actor,
        Guid companyId,
        Guid fileRecordId,
        CancellationToken cancellationToken);
}
