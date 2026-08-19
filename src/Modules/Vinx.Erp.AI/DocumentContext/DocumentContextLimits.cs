using Vinx.Erp.AI.Providers.OpenAiCompatible;

namespace Vinx.Erp.AI.DocumentContext;

/// <summary>
/// Limites do pipeline de contexto, compostos com o perfil de visão
/// vinculado: bytes codificados por página respeitam
/// <c>min(teto OCR, perfil.MaxImageBytesPerImage)</c> e uma página viaja por
/// chamada. Allowlist de classificação/consentimento é default deny.
/// </summary>
public sealed class DocumentContextLimits
{
    private const long DefaultOcrByteCap = 4L * 1024 * 1024;

    public int MaxPages { get; set; } = 20;

    /// <summary>Teto de pixels decodados por página/imagem (inspecionado antes do OCR).</summary>
    public long MaxPixelsPerPage { get; set; } = 4_000_000;

    /// <summary>Teto de bytes codificados por página: ≤ min(cap OCR, teto do perfil de visão).</summary>
    public long MaxEncodedBytesPerPage { get; set; } = DefaultOcrByteCap;

    public int MaxCharactersPerSegment { get; set; } = 20_000;

    public int MaxCharactersPerContext { get; set; } = 96_000;

    /// <summary>Timeout cooperativo da extração local (checagem por página).</summary>
    public TimeSpan ExtractionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public int RasterDpi { get; set; } = 150;

    /// <summary>Classificações consentidas para OCR; vazia nega tudo.</summary>
    public IReadOnlySet<string> AllowedClassifications { get; set; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Compõe os limites com o perfil de visão do #135-B.</summary>
    public static DocumentContextLimits FromVisionProfile(
        OpenAiCompatibleOptions visionProfile,
        IReadOnlySet<string> allowedClassifications) => new()
        {
            MaxEncodedBytesPerPage = Math.Min(DefaultOcrByteCap, visionProfile.MaxImageBytesPerImage),
            AllowedClassifications = allowedClassifications,
        };

    /// <summary>Validação determinística dos limites; mensagens fixas.</summary>
    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();

        if (MaxPages is < 1 or > 200)
        {
            failures.Add("page limit must be between 1 and 200");
        }

        if (MaxPixelsPerPage is < 1 or > 40_000_000)
        {
            failures.Add("pixel limit must be between 1 and 40000000");
        }

        if (MaxEncodedBytesPerPage is < 1_024 or > 20_971_520)
        {
            failures.Add("encoded byte limit must be between 1024 and 20971520");
        }

        if (MaxCharactersPerSegment is < 1 or > 100_000)
        {
            failures.Add("segment character limit must be between 1 and 100000");
        }

        if (MaxCharactersPerContext is < 1 or > 1_000_000)
        {
            failures.Add("context character limit must be between 1 and 1000000");
        }

        if (ExtractionTimeout < TimeSpan.FromSeconds(1) || ExtractionTimeout > TimeSpan.FromMinutes(10))
        {
            failures.Add("extraction timeout must be between 1s and 10m");
        }

        if (RasterDpi is < 72 or > 300)
        {
            failures.Add("raster dpi must be between 72 and 300");
        }

        if (AllowedClassifications.Count == 0)
        {
            failures.Add("classification allowlist must not be empty");
        }

        return failures;
    }
}
