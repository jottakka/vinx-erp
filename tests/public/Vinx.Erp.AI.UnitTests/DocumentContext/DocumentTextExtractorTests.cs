using Vinx.Erp.AI.DocumentContext;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.DocumentContext;

public sealed class DocumentTextExtractorTests
{
    private static DocumentContextLimits Approved(Action<DocumentContextLimits>? mutate = null)
    {
        var limits = DocumentContextLimits.FromVisionProfile(
            DocumentContextLimitsTests.VisionProfile(),
            new HashSet<string>(StringComparer.Ordinal) { "fiscal" });
        mutate?.Invoke(limits);
        return limits;
    }

    [Fact]
    public void Text_layer_pages_are_extracted_with_page_numbers()
    {
        using var pdf = new MemoryStream(
            TestDocumentFixtures.PdfWithText("pagina um", "pagina dois"));

        var pages = DocumentTextExtractor.Extract(pdf, Approved(), CancellationToken.None);

        Assert.Equal(2, pages.Count);
        Assert.Equal(1, pages[0].Page);
        Assert.Equal("pagina um", pages[0].Text);
        Assert.True(pages[0].HasTextLayer);
        Assert.Equal(2, pages[1].Page);
        Assert.Equal("pagina dois", pages[1].Text);
    }

    [Fact]
    public void Scanned_pages_report_no_text_layer()
    {
        using var pdf = new MemoryStream(TestDocumentFixtures.PdfWithoutText(2));

        var pages = DocumentTextExtractor.Extract(pdf, Approved(), CancellationToken.None);

        Assert.Equal(2, pages.Count);
        Assert.All(pages, page => Assert.False(page.HasTextLayer));
        Assert.All(pages, page => Assert.Equal(string.Empty, page.Text));
    }

    [Fact]
    public void Page_count_above_the_limit_is_rejected_before_any_raster()
    {
        using var pdf = new MemoryStream(TestDocumentFixtures.PdfWithText("a", "b"));

        var exception = Assert.Throws<DocumentContextValidationException>(
            () => DocumentTextExtractor.Extract(pdf, Approved(l => l.MaxPages = 1), CancellationToken.None));

        Assert.Contains("page limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_pdf_fails_safely_without_echoing_content()
    {
        using var pdf = new MemoryStream([0x25, 0x50, 0x44, 0x46, 0x2D, 0x21, 0x00, 0xFF, 0x99]);

        var exception = Assert.Throws<DocumentContextValidationException>(
            () => DocumentTextExtractor.Extract(pdf, Approved(), CancellationToken.None));

        Assert.Equal("document could not be parsed locally", exception.Message);
    }

    [Fact]
    public void Cancellation_is_cooperative_and_surfaces_as_operation_canceled()
    {
        using var pdf = new MemoryStream(TestDocumentFixtures.PdfWithText("a"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => DocumentTextExtractor.Extract(pdf, Approved(), cancelled.Token));
    }
}
