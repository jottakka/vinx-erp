using System.Net;
using System.Text;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.AI.Providers.OpenAiCompatible;
using Xunit;

namespace Vinx.Erp.AI.IntegrationTests;

/// <summary>
/// Acesso de nível fronteira do #134: a negação de leitura na fronteira
/// autorizada (empresa errada, membership revogada, estado não liberado —
/// semântica da #134-A) vira `NeedsClarification` fail-closed, sem chamada
/// ao provider e sem vazar qual foi o motivo interno. Os testes RLS-backed
/// contra Postgres real pertencem à #134-A, quando a fonte real existir.
/// </summary>
public sealed class DocumentContextAccessTests
{
    private static readonly IReadOnlySet<string> Classifications =
        new HashSet<string>(StringComparer.Ordinal) { "fiscal" };

    private static DocumentContextNormalizer CreateNormalizer(
        Func<Guid, AuthorizedDocument?> resolver,
        out CountingHandler handler)
    {
        handler = new CountingHandler();
        var profile = VisionProfile();
        return new DocumentContextNormalizer(
            new DenyingSource(resolver),
            new DocumentVisionOcr(new OpenAiCompatibleChatClient(profile, new HttpClient(handler))),
            DocumentContextLimits.FromVisionProfile(profile, Classifications));
    }

    private static OpenAiCompatibleOptions VisionProfile() => new()
    {
        Endpoint = new Uri("https://unit-test.invalid/v1/", UriKind.Absolute),
        Model = "vision-test-model",
        ApiKey = Guid.NewGuid().ToString("N"),
        AllowImageInputs = true,
    };

    [Fact]
    public async Task Company_mismatch_at_the_boundary_is_fail_closed()
    {
        // Documento existe, mas a fronteira nega (empresa Alfa para leitor Beta).
        var id = Guid.NewGuid();
        var normalizer = CreateNormalizer(_ => null, out var handler);

        var result = await normalizer.BuildContextAsync(id, CancellationToken.None);

        Assert.Equal(DocumentContextStatus.NeedsClarification, result.Status);
        Assert.Contains("not available", result.ClarificationReason, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Revoked_membership_is_fail_closed_on_every_new_read()
    {
        var id = Guid.NewGuid();
        var document = TextualDocument(id, "documento visivel");
        var revoked = false;
        var normalizer = CreateNormalizer(
            _ => revoked ? null : document,
            out var handler);

        var first = await normalizer.BuildContextAsync(id, CancellationToken.None);
        revoked = true;
        var second = await normalizer.BuildContextAsync(id, CancellationToken.None);

        Assert.Equal(DocumentContextStatus.Ready, first.Status);
        Assert.Equal(DocumentContextStatus.NeedsClarification, second.Status);
        Assert.Contains("not available", second.ClarificationReason, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Denied_read_never_leaks_the_internal_reason()
    {
        var id = Guid.NewGuid();
        var normalizer = CreateNormalizer(
            _ => throw new InvalidOperationException("RLS: empresa 7 membership revogada"), out _);

        var result = await normalizer.BuildContextAsync(id, CancellationToken.None);

        Assert.DoesNotContain("RLS", result.ClarificationReason ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("empresa", result.ClarificationReason ?? string.Empty, StringComparison.Ordinal);
    }

    private static AuthorizedDocument TextualDocument(Guid id, string text) =>
        new(id, Guid.NewGuid(), "application/pdf", "fiscal", new MemoryStream(TextualPdf(text)));

    private static byte[] TextualPdf(string text)
    {
        var content = $"BT /F1 12 Tf 50 780 Td ({text}) Tj ET";
        var objects = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Resources<</Font<</F1 4 0 R>>>>/Contents 5 0 R>>",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
            $"<</Length {content.Length}>>\nstream\n{content}\nendstream",
        };
        using var buffer = new MemoryStream();
        using var writer = new StreamWriter(buffer);
        writer.Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(buffer.Position);
            writer.Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            writer.Flush();
        }

        var xrefOffset = buffer.Position;
        writer.Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            writer.Write($"{offset:D10} 00000 n \n");
        }

        writer.Write($"trailer\n<</Size {objects.Count + 1}/Root 1 0 R>>\nstartxref\n{xrefOffset}\n%%EOF\n");
        writer.Flush();
        return buffer.ToArray();
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class DenyingSource(Func<Guid, AuthorizedDocument?> resolver)
        : IAuthorizedDocumentSource
    {
        public Task<AuthorizedDocument> OpenReadAsync(Guid fileRecordId, CancellationToken cancellationToken)
        {
            var document = resolver(fileRecordId);
            if (document is null)
            {
                throw new InvalidOperationException("denied");
            }

            return Task.FromResult(document);
        }
    }
}
