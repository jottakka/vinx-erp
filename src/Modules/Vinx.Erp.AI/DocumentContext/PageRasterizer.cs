using PDFtoImage;

namespace Vinx.Erp.AI.DocumentContext;

/// <summary>
/// Rasterização serializada de páginas de PDF via PDFtoImage (PDFium).
/// O PDFium não é thread-safe (chamadas globalmente serializadas): toda
/// rasterização passa por um gate único. Uma página por chamada; bytes
/// codificados respeitam o teto composto com degraus determinísticos de DPI
/// e rejeição segura ao final.
/// </summary>
public static class PageRasterizer
{
    private const int MinimumDpi = 72;

    // PDFium: gate global — sem paralelismo de renderização neste processo.
    private static readonly SemaphoreSlim PdfiumGate = new(1, 1);

    public static async Task<byte[]> RasterizePageAsync(
        Stream pdf,
        int page,
        double widthPoints,
        double heightPoints,
        DocumentContextLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(limits);
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "page must be 1-based");
        }

        // Preflight de pixels por DPI ANTES de qualquer render: uma página com
        // media box enorme e JPEG de baixa entropia passaria no teto de bytes
        // violando o teto de pixels.
        var safeDpis = DpiLadder(limits.RasterDpi)
            .Where(dpi => PixelsForDpi(widthPoints, heightPoints, dpi) <= limits.MaxPixelsPerPage)
            .ToArray();
        if (safeDpis.Length == 0)
        {
            throw new DocumentContextValidationException("page raster exceeds the approved pixel limit");
        }

        await PdfiumGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!pdf.CanSeek)
            {
                throw new DocumentContextValidationException("document stream must be seekable");
            }

            return await Task.Run(() =>
            {
                // Nativos PDFium: plataformas suportadas (Linux no host,
                // macOS/Windows em desenvolvimento). Fora delas, falha segura.
                if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
                {
                    throw new DocumentContextValidationException("rasterization is not supported on this platform");
                }

                foreach (var dpi in safeDpis)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    byte[] bytes;
                    try
                    {
                        pdf.Position = 0;
                        using var output = new MemoryStream();
#pragma warning disable CA1416 // Union de plataformas suportadas pelo PDFium cobre Linux (deploy), macOS/Windows (dev); guard explícito acima.
                        Conversion.SaveJpeg(pdf, output, page - 1, false, null, new RenderOptions { Dpi = dpi });
#pragma warning restore CA1416
                        bytes = output.ToArray();
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // PDFium rejeita documentos corrompidos: falha segura
                        // sem ecoar conteúdo.
                        throw new DocumentContextValidationException("document could not be rendered locally");
                    }

                    if (bytes.Length <= limits.MaxEncodedBytesPerPage)
                    {
                        return bytes;
                    }
                }

                throw new DocumentContextValidationException(
                    "page raster exceeds the approved encoded byte limit");
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PdfiumGate.Release();
        }
    }

    // Pixels resultantes de uma página de w×h pontos renderizada em dpi.
    private static long PixelsForDpi(double widthPoints, double heightPoints, int dpi)
    {
        var widthPixels = (long)Math.Ceiling(widthPoints * dpi / 72.0);
        var heightPixels = (long)Math.Ceiling(heightPoints * dpi / 72.0);
        return widthPixels * heightPixels;
    }

    // Degraus determinísticos de DPI: aprovado, metade, mínimo. Sem silent
    // upscale/downscale infinito: ao final, rejeição segura.
    private static IEnumerable<int> DpiLadder(int approvedDpi)
    {
        yield return approvedDpi;
        var half = approvedDpi / 2;
        if (half >= MinimumDpi)
        {
            yield return half;
        }

        if (MinimumDpi != approvedDpi && MinimumDpi != half)
        {
            yield return MinimumDpi;
        }
    }
}
