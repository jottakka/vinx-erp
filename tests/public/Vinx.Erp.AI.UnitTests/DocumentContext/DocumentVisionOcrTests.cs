using Microsoft.Extensions.AI;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.AI.Providers.OpenAiCompatible;
using Vinx.Erp.AI.UnitTests.Providers;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.DocumentContext;

public sealed class DocumentVisionOcrTests
{
    private static DocumentVisionOcr CreateOcr(TestProviderHandler handler) =>
        new(new OpenAiCompatibleChatClient(
            DocumentContextLimitsTests.VisionProfile(), new HttpClient(handler)));

    [Fact]
    public void Parse_strict_accepts_the_exact_schema()
    {
        var result = DocumentVisionOcr.ParseStrict("""{"text":"fatura 123","confidence":0.87,"language":"pt"}""");

        Assert.NotNull(result);
        Assert.Equal("fatura 123", result!.Text);
        Assert.Equal(0.87, result.Confidence);
        Assert.Equal("pt", result.Language);
    }

    [Theory]
    [InlineData("""{"confidence":0.5,"language":"pt"}""")]                       // sem text
    [InlineData("""{"text":"x","confidence":1.5,"language":"pt"}""")]            // confidence > 1
    [InlineData("""{"text":"x","confidence":-0.1,"language":"pt"}""")]           // confidence < 0
    [InlineData("""{"text":1,"confidence":0.5,"language":"pt"}""")]              // text não-string
    [InlineData("""["text"]""")]                                                  // não-objeto
    [InlineData("""{"text":"x","confidence":0.5,}""")]                            // trailing comma
    [InlineData("")]
    public void Parse_strict_rejects_invalid_schemas(string json)
    {
        Assert.Null(DocumentVisionOcr.ParseStrict(json));
    }

    [Fact]
    public async Task Transcribe_sends_one_image_and_parses_the_strict_response()
    {
        var handler = TestProviderHandler.RespondsWith(
            TestProviderHandler.ChatCompletionJson("""{"text":"pagina transcrita","confidence":0.9,"language":"pt"}"""));
        var ocr = CreateOcr(handler);

        var result = await ocr.TranscribePageAsync([1, 2, 3], "image/png", CancellationToken.None);

        Assert.Equal("pagina transcrita", result.Text);
        var wire = handler.RequestBodies[^1];
        Assert.Contains("image_url", wire, StringComparison.Ordinal);
        Assert.Contains("data:image/png", wire, StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Non_json_provider_response_fails_safely()
    {
        var handler = TestProviderHandler.RespondsWith(
            TestProviderHandler.ChatCompletionJson("texto solto sem JSON"));
        var ocr = CreateOcr(handler);

        var exception = await Assert.ThrowsAsync<DocumentContextValidationException>(
            () => ocr.TranscribePageAsync([1], "image/png", CancellationToken.None));

        Assert.Contains("valid JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_failure_surfaces_as_provider_exception_without_retry()
    {
        var handler = TestProviderHandler.RespondsWith("{}", statusCode: 500);
        var ocr = CreateOcr(handler);

        var exception = await Assert.ThrowsAsync<OpenAiCompatibleProviderException>(
            () => ocr.TranscribePageAsync([1], "image/png", CancellationToken.None));

        Assert.Equal(OpenAiCompatibleFailureKind.ProviderUnavailable, exception.Kind);
        Assert.Equal(1, handler.CallCount);
    }
}
