using Vinx.Erp.AI.DocumentContext;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.DocumentContext;

public sealed class ImageCanonicalizerTests
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
    public void Direct_image_is_re_encoded_without_original_bytes_or_metadata()
    {
        var original = TestDocumentFixtures.NoisyPng(64, 64);

        var canonical = ImageCanonicalizer.Canonicalize(original, Approved());

        Assert.Contains(canonical.MediaType, ["image/png", "image/jpeg"], StringComparer.Ordinal);
        Assert.NotEqual(original, canonical.Bytes);
        Assert.True(canonical.Bytes.Length > 0);
    }

    [Fact]
    public void Noisy_image_falls_back_to_lossy_encoding_within_the_byte_cap()
    {
        var noisy = TestDocumentFixtures.NoisyPng(400, 400);

        var canonical = ImageCanonicalizer.Canonicalize(
            noisy,
            Approved(l => l.MaxEncodedBytesPerPage = 300_000));

        Assert.Equal("image/jpeg", canonical.MediaType);
        Assert.True(canonical.Bytes.Length <= 300_000);
    }

    [Fact]
    public void Image_above_the_pixel_limit_is_rejected()
    {
        var large = TestDocumentFixtures.Png(3000, 2000);

        var exception = Assert.Throws<DocumentContextValidationException>(
            () => ImageCanonicalizer.Canonicalize(large, Approved()));

        Assert.Contains("pixel limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_above_both_encoding_budgets_is_rejected()
    {
        var noisy = TestDocumentFixtures.NoisyPng(500, 500);

        var exception = Assert.Throws<DocumentContextValidationException>(
            () => ImageCanonicalizer.Canonicalize(
                noisy,
                Approved(l => l.MaxEncodedBytesPerPage = 1_024)));

        Assert.Contains("encoded byte limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Undecodable_image_fails_safely()
    {
        var exception = Assert.Throws<DocumentContextValidationException>(
            () => ImageCanonicalizer.Canonicalize([1, 2, 3, 4], Approved()));

        Assert.Contains("decoded", exception.Message, StringComparison.Ordinal);
    }
}
