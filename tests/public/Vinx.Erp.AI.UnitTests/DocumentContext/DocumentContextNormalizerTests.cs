using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.AI.Providers.OpenAiCompatible;
using Vinx.Erp.AI.UnitTests.Providers;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.DocumentContext;

public sealed class DocumentContextNormalizerTests
{
    private static readonly IReadOnlySet<string> Classifications =
        new HashSet<string>(StringComparer.Ordinal) { "fiscal" };

    private static DocumentContextNormalizer CreateNormalizer(
        FakeAuthorizedDocumentSource source,
        TestProviderHandler handler,
        Action<DocumentContextLimits>? mutateLimits = null)
    {
        var limits = DocumentContextLimits.FromVisionProfile(
            DocumentContextLimitsTests.VisionProfile(), Classifications);
        mutateLimits?.Invoke(limits);
        return new DocumentContextNormalizer(
            source,
            new DocumentVisionOcr(new OpenAiCompatibleChatClient(
                DocumentContextLimitsTests.VisionProfile(), new HttpClient(handler))),
            limits);
    }

    private static string OcrJson(string text, double confidence = 0.9) =>
        $$"""{"text":"{{text}}","confidence":{{confidence.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}},"language":"pt"}""";

    [Fact]
    public async Task Text_pdf_produces_ready_context_without_any_provider_call()
    {
        var id = Guid.NewGuid();
        var source = new FakeAuthorizedDocumentSource(
            _ => FakeAuthorizedDocumentSource.Clean(id, "application/pdf", TestDocumentFixtures.PdfWithText("fatura alpha")));
        var handler = TestProviderHandler.RespondsWith("{}");
        var normalizer = CreateNormalizer(source, handler);

        var result = await normalizer.BuildContextAsync(id, CancellationToken.None);

        Assert.Equal(DocumentContextStatus.Ready, result.Status);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(1, segment.Page);
        Assert.Equal(DocumentSegmentSource.TextLayer, segment.Source);
        Assert.Equal(1.0, segment.Confidence);
        Assert.Equal($"{id:N}:p1", segment.SourceRef);
        Assert.True(segment.Untrusted);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Scanned_pdf_rasterizes_and_ocrs_each_page_without_text()
    {
        if (!PdfiumEnvironment.IsAvailable)
        {
            return; // sandbox local sem PDFium funcional; CI linux-x64 executa.
        }

        var id = Guid.NewGuid();
        var source = new FakeAuthorizedDocumentSource(
            _ => FakeAuthorizedDocumentSource.Clean(id, "application/pdf", TestDocumentFixtures.PdfWithoutText(2)));
        var handler = TestProviderHandler.RespondsWith(
            TestProviderHandler.ChatCompletionJson(OcrJson("pagina transcrita")));
        var normalizer = CreateNormalizer(source, handler);

        var result = await normalizer.BuildContextAsync(id, CancellationToken.None);

        Assert.Equal(DocumentContextStatus.Ready, result.Status);
        Assert.Equal(2, result.Segments.Count);
        Assert.All(result.Segments, segment =>
        {
            Assert.Equal(DocumentSegmentSource.VisionOcr, segment.Source);
            Assert.Equal("pagina transcrita", segment.Text);
        });
        // Uma página por chamada: exatamente duas chamadas ao provider.
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Mixed_pdf_uses_text_layer_and_vision_per_page()
    {
        if (!PdfiumEnvironment.IsAvailable)
        {
            return; // sandbox local sem PDFium funcional; CI linux-x64 executa.
        }

        var id = Guid.NewGuid();
        // Página 1 com texto (content stream); página 2 em branco (sem camada).
        var mixed = TestDocumentFixtures.MinimalPdf("pagina com texto", null);
        var source = new FakeAuthorizedDocumentSource(
            _ => FakeAuthorizedDocumentSource.Clean(id, "application/pdf", mixed));
        var handler = TestProviderHandler.RespondsWith(
            TestProviderHandler.ChatCompletionJson(OcrJson("ocr")));
        var normalizer = CreateNormalizer(source, handler);

        var result = await normalizer.BuildContextAsync(id, CancellationToken.None);

        Assert.Equal(DocumentContextStatus.Ready, result.Status);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(DocumentSegmentSource.TextLayer, result.Segments[0].Source);
        Assert.Equal(DocumentSegmentSource.VisionOcr, result.Segments[1].Source);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Direct_image_is_canonicalized_and_ocrd_as_a_single_page()
    {
        var id = Guid.NewGuid();
        var source = new FakeAuthorizedDocumentSource(
            _ => FakeAuthorizedDocumentSource.Clean(id, "image/png", TestDocumentFixtures.Png(200, 200)));
        var handler = TestProviderHandler.RespondsWith(
            TestProviderHandler.ChatCompletionJson(OcrJson("legenda")));
        var normalizer = CreateNormalizer(source, handler);

        var result = await normalizer.BuildContextAsync(id, CancellationToken.None);

        Assert.Equal(DocumentContextStatus.Ready, result.Status);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(1, segment.Page);
        Assert.Equal(DocumentSegmentSource.VisionOcr, segment.Source);
        // O provider recebeu a forma canônica (JPEG de queda ou PNG), nunca o
        // arquivo original byte a byte.
        var wire = handler.RequestBodies[^1];
        Assert.DoesNotContain(Convert.ToBase64String(TestDocumentFixtures.Png(200, 200)), wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Classification_outside_the_allowlist_is_denied_before_any_work()
    {
        var id = Guid.NewGuid();
        var source = new FakeAuthorizedDocumentSource(
            _ => FakeAuthorizedDocumentSource.Clean(
                id, "image/png", TestDocumentFixtures.Png(50, 50), classification: "medico"));
        var handler = TestProviderHandler.RespondsWith("{}");
        var normalizer = CreateNormalizer(source, handler);

        var result = await normalizer.BuildContextAsync(id, CancellationToken.None);

        Assert.Equal(DocumentContextStatus.NeedsClarification, result.Status);
        Assert.Contains("classification", result.ClarificationReason, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
        Assert.Equal(1, source.OpenAttempts);
    }

    [Fact]
    public async Task Source_denial_is_fail_closed()
    {
        var id = Guid.NewGuid();
        var source = new FakeAuthorizedDocumentSource(_ => null);
        var handler = TestProviderHandler.RespondsWith("{}");
        var normalizer = CreateNormalizer(source, handler);

        var result = await normalizer.BuildContextAsync(id, CancellationToken.None);

        Assert.Equal(DocumentContextStatus.NeedsClarification, result.Status);
        Assert.Contains("not available", result.ClarificationReason, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Unsupported_media_type_is_needs_clarification()
    {
        var id = Guid.NewGuid();
        var source = new FakeAuthorizedDocumentSource(
            _ => FakeAuthorizedDocumentSource.Clean(id, "application/zip", [1, 2, 3]));
        var handler = TestProviderHandler.RespondsWith("{}");
        var normalizer = CreateNormalizer(source, handler);

        var result = await normalizer.BuildContextAsync(id, CancellationToken.None);

        Assert.Equal(DocumentContextStatus.NeedsClarification, result.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Malformed_pdf_is_needs_clarification()
    {
        var id = Guid.NewGuid();
        var source = new FakeAuthorizedDocumentSource(
            _ => FakeAuthorizedDocumentSource.Clean(id, "application/pdf", [1, 2, 3, 4, 5]));
        var handler = TestProviderHandler.RespondsWith("{}");
        var normalizer = CreateNormalizer(source, handler);

        var result = await normalizer.BuildContextAsync(id, CancellationToken.None);

        Assert.Equal(DocumentContextStatus.NeedsClarification, result.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Invalid_vision_json_is_needs_clarification()
    {
        if (!PdfiumEnvironment.IsAvailable)
        {
            return; // sandbox local sem PDFium funcional; CI linux-x64 executa.
        }

        var id = Guid.NewGuid();
        var source = new FakeAuthorizedDocumentSource(
            _ => FakeAuthorizedDocumentSource.Clean(id, "application/pdf", TestDocumentFixtures.PdfWithoutText(1)));
        var handler = TestProviderHandler.RespondsWith(
            TestProviderHandler.ChatCompletionJson("nao sou json"));
        var normalizer = CreateNormalizer(source, handler);

        var result = await normalizer.BuildContextAsync(id, CancellationToken.None);

        Assert.Equal(DocumentContextStatus.NeedsClarification, result.Status);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Context_is_truncated_deterministically_at_the_character_limit()
    {
        var id = Guid.NewGuid();
        var source = new FakeAuthorizedDocumentSource(
            _ => FakeAuthorizedDocumentSource.Clean(id, "application/pdf", TestDocumentFixtures.PdfWithText("texto longo")));
        var handler = TestProviderHandler.RespondsWith("{}");
        var normalizer = CreateNormalizer(source, handler, l => l.MaxCharactersPerContext = 4);

        var result = await normalizer.BuildContextAsync(id, CancellationToken.None);

        Assert.Equal(DocumentContextStatus.Ready, result.Status);
        Assert.True(result.Truncated);
        Assert.Equal(4, result.TotalCharacters);
    }

    [Fact]
    public async Task Nothing_is_retained_between_reads()
    {
        var id = Guid.NewGuid();
        var source = new FakeAuthorizedDocumentSource(
            _ => FakeAuthorizedDocumentSource.Clean(id, "application/pdf", TestDocumentFixtures.PdfWithText("efemero")));
        var handler = TestProviderHandler.RespondsWith("{}");
        var normalizer = CreateNormalizer(source, handler);

        await normalizer.BuildContextAsync(id, CancellationToken.None);
        await normalizer.BuildContextAsync(id, CancellationToken.None);

        // Sem cache: a fronteira é chamada novamente e o contexto reprocessado.
        Assert.Equal(2, source.OpenAttempts);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_needs_clarification()
    {
        var id = Guid.NewGuid();
        var source = new FakeAuthorizedDocumentSource(
            _ => FakeAuthorizedDocumentSource.Clean(id, "application/pdf", TestDocumentFixtures.PdfWithText("x")));
        var handler = TestProviderHandler.RespondsWith("{}");
        var normalizer = CreateNormalizer(source, handler);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => normalizer.BuildContextAsync(id, cancelled.Token));
    }

    [Fact]
    public async Task Failing_content_stream_is_needs_clarification_with_zero_provider_calls()
    {
        var id = Guid.NewGuid();
        var source = new FakeAuthorizedDocumentSource(
            _ => new AuthorizedDocument(
                id, Guid.NewGuid(), "image/png", "fiscal", new ThrowingStream()));
        var handler = TestProviderHandler.RespondsWith("{}");
        var normalizer = CreateNormalizer(source, handler);

        var result = await normalizer.BuildContextAsync(id, CancellationToken.None);

        Assert.Equal(DocumentContextStatus.NeedsClarification, result.Status);
        Assert.Contains("could not be produced safely", result.ClarificationReason, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("stream falhou");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Constructor_rejects_limits_that_fail_validation()
    {
        var source = new FakeAuthorizedDocumentSource(_ => null);
        var handler = TestProviderHandler.RespondsWith("{}");

        Assert.Throws<DocumentContextValidationException>(
            () => new DocumentContextNormalizer(
                source,
                new DocumentVisionOcr(new OpenAiCompatibleChatClient(
                    DocumentContextLimitsTests.VisionProfile(), new HttpClient(handler))),
                DocumentContextLimits.FromVisionProfile(
                    DocumentContextLimitsTests.VisionProfile(),
                    new HashSet<string>(StringComparer.Ordinal))));
    }
}
