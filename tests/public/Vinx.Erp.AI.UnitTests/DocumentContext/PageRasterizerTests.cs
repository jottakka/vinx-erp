using Vinx.Erp.AI.DocumentContext;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.DocumentContext;

public sealed class PageRasterizerTests
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
    public async Task Scanned_page_rasterizes_within_the_composed_byte_cap()
    {
        if (!PdfiumEnvironment.IsAvailable)
        {
            return; // sandbox local sem PDFium funcional; CI linux-x64 executa.
        }

        using var pdf = new MemoryStream(TestDocumentFixtures.PdfWithoutText(1));
        var limits = Approved();

        var jpeg = await PageRasterizer.RasterizePageAsync(pdf, 1, limits, CancellationToken.None);

        Assert.True(jpeg.Length > 0);
        Assert.True(jpeg.Length <= limits.MaxEncodedBytesPerPage);
    }

    [Fact]
    public async Task Raster_above_the_byte_cap_after_the_dpi_ladder_fails_safely()
    {
        if (!PdfiumEnvironment.IsAvailable)
        {
            return; // sandbox local sem PDFium funcional; CI linux-x64 executa.
        }

        using var pdf = new MemoryStream(TestDocumentFixtures.PdfWithoutText(1));

        var exception = await Assert.ThrowsAsync<DocumentContextValidationException>(
            () => PageRasterizer.RasterizePageAsync(
                pdf, 1, Approved(l => l.MaxEncodedBytesPerPage = 1_024), CancellationToken.None));

        Assert.Contains("encoded byte limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_zero_is_rejected_as_argument()
    {
        using var pdf = new MemoryStream(TestDocumentFixtures.PdfWithoutText(1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => PageRasterizer.RasterizePageAsync(pdf, 0, Approved(), CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_operation_canceled()
    {
        using var pdf = new MemoryStream(TestDocumentFixtures.PdfWithoutText(1));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PageRasterizer.RasterizePageAsync(pdf, 1, Approved(), cancelled.Token));
    }
}
