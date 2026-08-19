using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.AI.Providers.OpenAiCompatible;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.DocumentContext;

public sealed class DocumentContextLimitsTests
{
    private static IReadOnlySet<string> Classifications { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "fiscal" };

    private static DocumentContextLimits Approved(Action<DocumentContextLimits>? mutate = null)
    {
        var limits = DocumentContextLimits.FromVisionProfile(VisionProfile(), Classifications);
        mutate?.Invoke(limits);
        return limits;
    }

    internal static OpenAiCompatibleOptions VisionProfile(Action<OpenAiCompatibleOptions>? mutate = null)
    {
        var options = new OpenAiCompatibleOptions
        {
            Endpoint = new Uri("https://unit-test.invalid/v1/", UriKind.Absolute),
            Model = "vision-test-model",
            ApiKey = Guid.NewGuid().ToString("N"),
            AllowImageInputs = true,
        };
        mutate?.Invoke(options);
        return options;
    }

    [Fact]
    public void Approved_limits_pass_validation()
    {
        Assert.Empty(Approved().Validate());
    }

    [Fact]
    public void Encoded_byte_limit_is_composed_with_the_vision_profile()
    {
        // teto composto: min(cap OCR 4 MiB, teto do perfil).
        var composed = DocumentContextLimits.FromVisionProfile(
            VisionProfile(o => o.MaxImageBytesPerImage = 1024 * 1024),
            Classifications);

        Assert.Equal(1024 * 1024, composed.MaxEncodedBytesPerPage);

        var overOcrCap = DocumentContextLimits.FromVisionProfile(
            VisionProfile(o => o.MaxImageBytesPerImage = 20L * 1024 * 1024),
            Classifications);

        Assert.Equal(4L * 1024 * 1024, overOcrCap.MaxEncodedBytesPerPage);
    }

    [Fact]
    public void Empty_classification_allowlist_fails_validation()
    {
        var limits = Approved(l => l.AllowedClassifications = new HashSet<string>(StringComparer.Ordinal));

        Assert.Contains(
            "classification allowlist",
            string.Join(';', limits.Validate()),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void Page_limit_out_of_range_fails(int maxPages)
    {
        var limits = Approved(l => l.MaxPages = maxPages);

        Assert.Contains("page limit", string.Join(';', limits.Validate()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void Raster_dpi_out_of_range_fails(int dpi)
    {
        var limits = Approved(l => l.RasterDpi = dpi);

        Assert.Contains("raster dpi", string.Join(';', limits.Validate()), StringComparison.Ordinal);
    }

    [Fact]
    public void Extraction_timeout_out_of_range_fails()
    {
        var limits = Approved(l => l.ExtractionTimeout = TimeSpan.FromMilliseconds(500));

        Assert.Contains("extraction timeout", string.Join(';', limits.Validate()), StringComparison.Ordinal);
    }

    [Fact]
    public void Pixel_limit_out_of_range_fails()
    {
        var limits = Approved(l => l.MaxPixelsPerPage = 0);

        Assert.Contains("pixel limit", string.Join(';', limits.Validate()), StringComparison.Ordinal);
    }
}
