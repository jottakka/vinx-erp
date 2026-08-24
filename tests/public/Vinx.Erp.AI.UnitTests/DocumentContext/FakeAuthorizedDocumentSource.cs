using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.AI.UnitTests.DocumentContext;

/// <summary>
/// Fonte fake controlada: substitui a fronteira #134-A (RLS/membership/
/// capability/estado/retenção). Documento ausente simula a negação da
/// fronteira (empresa errada, membership revogada, estado não liberado).
/// </summary>
internal sealed class FakeAuthorizedDocumentSource(Func<Guid, AuthorizedDocument?> resolver)

    : IAuthorizedDocumentSource
{
    public int OpenAttempts { get; private set; }

    public Task<AuthorizedDocument> OpenReadAsync(
        ActorContext actor,
        Guid companyId,
        Guid fileRecordId,
        CancellationToken cancellationToken)
    {
        OpenAttempts++;
        var document = resolver(fileRecordId) ?? throw new InvalidOperationException("denied by the fake boundary");
        return Task.FromResult(document);
    }

    internal static AuthorizedDocument Clean(
        Guid fileRecordId,
        string mediaType,
        byte[] content,
        string classification = "fiscal") =>
        new(fileRecordId, Guid.NewGuid(), mediaType, classification, new MemoryStream(content));
}
