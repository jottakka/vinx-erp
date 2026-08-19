using Microsoft.Extensions.Options;
using Vinx.Erp.AI.Providers.DeepSeek;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Providers.DeepSeek;

public sealed class DeepSeekOptionsTests
{
    private static DeepSeekOptions Approved()
    {
        // Chave sintética por teste: nenhum fixture contém segredo real.
        return new DeepSeekOptions { ApiKey = Guid.NewGuid().ToString("N") };
    }

    private static ValidateOptionsResult Validate(DeepSeekOptions options) =>
        new DeepSeekOptions.Validator().Validate(nameof(DeepSeekOptions), options);

    [Fact]
    public void Approved_profile_passes_validation()
    {
        var result = Validate(Approved());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Approved_profile_uses_https_deepseek_endpoint_and_flash_model()
    {
        var options = Approved();

        Assert.Equal("https", options.Endpoint.Scheme);
        Assert.Equal("deepseek-v4-flash", options.Model);
    }

    [Theory]
    [InlineData("http://api.deepseek.com/v1/", "endpoint must be absolute https")]
    [InlineData("", "model must be a non-empty approved model name")]
    [InlineData("   ", "model must be a non-empty approved model name")]
    public void Invalid_profiles_fail_validation(string invalidValue, string expectedFragment)
    {
        var options = Approved();
        if (Uri.TryCreate(invalidValue, UriKind.Absolute, out var endpoint))
        {
            options.Endpoint = endpoint;
        }
        else
        {
            options.Model = invalidValue;
        }

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains(expectedFragment, string.Join("; ", result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_api_key_fails_validation_without_echoing_the_key()
    {
        var options = Approved();
        var syntheticKey = options.ApiKey;
        options.ApiKey = "";

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.DoesNotContain(syntheticKey, string.Join("; ", result.Failures), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    [InlineData(301_000)]
    public void Timeout_outside_approved_range_fails(int timeoutMilliseconds)
    {
        var options = Approved();
        options.Timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);

        var result = Validate(options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Budget_below_output_token_limit_fails()
    {
        var options = Approved();
        options.MaxTotalTokensPerCall = options.MaxOutputTokens - 1;

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains("budget", string.Join("; ", result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Null_options_fails_validation()
    {
        var result = new DeepSeekOptions.Validator().Validate(nameof(DeepSeekOptions), null!);

        Assert.True(result.Failed);
    }
}
