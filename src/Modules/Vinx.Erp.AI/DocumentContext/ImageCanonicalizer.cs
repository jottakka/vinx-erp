using SkiaSharp;

namespace Vinx.Erp.AI.DocumentContext;

/// <summary>Imagem canônica pronta para o perfil de visão.</summary>
public sealed record CanonicalImage(byte[] Bytes, string MediaType);

/// <summary>
/// Canonicalização de imagem direta: decode → teto de pixels → strip de
/// metadados → re-encode determinístico. O provider nunca recebe os bytes
/// originais nem metadados (EXIF descartados pelo re-encode). Sem silent
/// downscale: acima do teto de pixels é rejeição determinística.
/// </summary>
public static class ImageCanonicalizer
{
    private const int JpegQuality = 80;

    public static CanonicalImage Canonicalize(
        byte[] image,
        DocumentContextLimits limits)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(limits);

        SKBitmap? decoded;
        try
        {
            decoded = SKBitmap.Decode(image);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Skia pode lançar (codec) em bytes ilegíveis em vez de devolver null.
            throw new DocumentContextValidationException("image could not be decoded");
        }

        // Recurso nativo: descarte determinístico.
        using var bitmap = decoded;
        if (bitmap is null)
        {
            throw new DocumentContextValidationException("image could not be decoded");
        }

        if ((long)bitmap.Width * bitmap.Height > limits.MaxPixelsPerPage)
        {
            throw new DocumentContextValidationException("image exceeds the approved pixel limit");
        }

        var lossless = Encode(bitmap, SKEncodedImageFormat.Png, 100);
        if (lossless.Length <= limits.MaxEncodedBytesPerPage)
        {
            return new CanonicalImage(lossless, "image/png");
        }

        var lossy = Encode(bitmap, SKEncodedImageFormat.Jpeg, JpegQuality);
        if (lossy.Length <= limits.MaxEncodedBytesPerPage)
        {
            return new CanonicalImage(lossy, "image/jpeg");
        }

        throw new DocumentContextValidationException("image exceeds the approved encoded byte limit");
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var encoded = bitmap.Encode(format, quality);
        return encoded.ToArray();
    }
}
