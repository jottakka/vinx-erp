using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Vinx.Erp.AI.UnitTests.DocumentContext;

/// <summary>
/// Fixtures sintéticos de documento gerados em código: nenhum arquivo real,
/// nenhum dado médico, nenhum segredo.
/// </summary>
internal static class TestDocumentFixtures
{
    public static byte[] PdfWithText(params string[] pageTexts)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(595, 842);
            page.AddText(text, 12, new PdfPoint(50, 780), font);
        }

        return builder.Build();
    }

    public static byte[] PdfWithoutText(int pages, double width = 595, double height = 842) =>
        BuildPdf(width, height, Enumerable.Repeat<string?>(null, pages).ToArray());

    public static byte[] MinimalPdf(params string?[] pageTexts) =>
        BuildPdf(595, 842, pageTexts);

    /// <summary>
    /// PDF mínimo canônico (xref computado em código): carregável tanto pelo
    /// PdfPig (leitura) quanto pelo PDFium (rasterização) — o writer do PdfPig
    /// não é aceito pelo PDFium. Página com texto usa content stream Tj;
    /// página nula fica em branco (sem camada de texto). Texto ASCII simples
    /// sem parênteses.
    /// </summary>
    private static byte[] BuildPdf(double width, double height, params string?[] pageTexts)
    {
        var pageCount = pageTexts.Length;
        var hasFont = pageTexts.Any(text => text is not null);
        var fontId = 3 + pageCount;
        var contentIds = new int?[pageCount];
        var nextId = fontId + (hasFont ? 1 : 0);
        for (var i = 0; i < pageCount; i++)
        {
            if (pageTexts[i] is not null)
            {
                contentIds[i] = nextId;
                nextId++;
            }
        }

        var totalObjects = nextId - 1;
        using var buffer = new MemoryStream();
        var offsets = new long[totalObjects + 1];

        Write(buffer, "%PDF-1.4\n");
        offsets[1] = buffer.Position;
        Write(buffer, "1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n");
        offsets[2] = buffer.Position;
        Write(buffer, $"2 0 obj\n<</Type/Pages/Kids[{string.Join(' ', Enumerable.Range(3, pageCount).Select(i => $"{i} 0 R"))}]/Count {pageCount}>>\nendobj\n");

        for (var i = 0; i < pageCount; i++)
        {
            offsets[3 + i] = buffer.Position;
            var resources = hasFont ? $"/Resources<</Font<</F1 {fontId} 0 R>>>>" : "/Resources<<>>";
            var contents = contentIds[i] is int id ? $"/Contents {id} 0 R" : string.Empty;
            Write(buffer, $"{3 + i} 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 {width.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} {height.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}]{resources}{contents}>>\nendobj\n");
        }

        if (hasFont)
        {
            offsets[fontId] = buffer.Position;
            Write(buffer, $"{fontId} 0 obj\n<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>\nendobj\n");
        }

        for (var i = 0; i < pageCount; i++)
        {
            if (contentIds[i] is int id)
            {
                offsets[id] = buffer.Position;
                var stream = $"BT /F1 12 Tf 50 780 Td ({pageTexts[i]}) Tj ET";
                Write(buffer, $"{id} 0 obj\n<</Length {stream.Length}>>\nstream\n{stream}\nendstream\nendobj\n");
            }
        }

        var xrefOffset = buffer.Position;
        Write(buffer, $"xref\n0 {totalObjects + 1}\n0000000000 65535 f \n");
        for (var id = 1; id <= totalObjects; id++)
        {
            Write(buffer, $"{offsets[id]:D10} 00000 n \n");
        }

        Write(buffer, $"trailer\n<</Size {totalObjects + 1}/Root 1 0 R>>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return buffer.ToArray();

        static void Write(Stream stream, string text)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    public static byte[] Png(int width, int height, int seed = 42)
    {
        using var bitmap = CreateBitmap(width, height, noise: false, seed);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    public static byte[] NoisyPng(int width, int height, int seed = 42)
    {
        using var bitmap = CreateBitmap(width, height, noise: true, seed);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static SKBitmap CreateBitmap(int width, int height, bool noise, int seed)
    {
        var bitmap = new SKBitmap(width, height);
        if (!noise)
        {
            bitmap.Erase(SKColors.White);
            return bitmap;
        }

        // Ruído determinístico de pixel para fixture (não é uso criptográfico):
        // seeded para o tamanho do PNG/JPEG ser estável entre execuções.
        var random = new Random(seed);
        var pixels = bitmap.Pixels;
        for (var i = 0; i < pixels.Length; i++)
        {
            var value = (byte)random.Next(256);
            pixels[i] = new SKColor(value, value, value);
        }

        bitmap.Pixels = pixels;
        return bitmap;
    }
}
