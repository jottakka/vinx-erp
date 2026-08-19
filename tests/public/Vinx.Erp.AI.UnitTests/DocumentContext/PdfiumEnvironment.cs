using PDFtoImage;

namespace Vinx.Erp.AI.UnitTests.DocumentContext;

/// <summary>
/// Canário de ambiente: o nativo do PDFium não carrega em sandbox local
/// (dylib presente, load bloqueado — verificado com PDF real do sistema).
/// Testes de rasterização executam integralmente no CI linux-x64; localmente
/// sem PDFium funcional eles são neutros por guarda, não por omissão.
/// </summary>
internal static class PdfiumEnvironment
{
    private static readonly Lazy<bool> Available = new(Check);

    public static bool IsAvailable => Available.Value;

    private static bool Check()
    {
        try
        {
            using var pdf = new MemoryStream(TestDocumentFixtures.MinimalPdf([null]));
            using var output = new MemoryStream();
#pragma warning disable CA1416 // Canário de ambiente: PDFium cobre Linux/macOS/Windows.
            Conversion.SaveJpeg(pdf, output, 0, false, null, new RenderOptions { Dpi = 72 });
#pragma warning restore CA1416
            return output.ToArray().Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
