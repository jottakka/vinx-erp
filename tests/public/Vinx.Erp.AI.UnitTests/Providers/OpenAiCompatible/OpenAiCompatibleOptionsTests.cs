using Microsoft.Extensions.Options;
using Vinx.Erp.AI.Providers.OpenAiCompatible;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Providers.OpenAiCompatible;

public sealed class OpenAiCompatibleOptionsTests
{
    private static OpenAiCompatibleOptions Approved(Action<OpenAiCompatibleOptions>? mutate = null)
    {
        // Chave sintética por teste: nenhum fixture contém segredo real.
        var options = new OpenAiCompatibleOptions
        {
            Endpoint = new Uri("https://unit-test.invalid/v1/", UriKind.Absolute),
            Model = "test-model",
            ApiKey = Guid.NewGuid().ToString("N"),
        };
        mutate?.Invoke(options);
        return options;
    }

    private static ValidateOptionsResult Validate(OpenAiCompatibleOptions options) =>
        new OpenAiCompatibleOptions.Validator().Validate(nameof(OpenAiCompatibleOptions), options);

    [Fact]
    public void Approved_profile_passes_validation()
    {
        var result = Validate(Approved());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Vision_profile_with_caps_inside_the_budget_passes_validation()
    {
        var result = Validate(Approved(o =>
        {
            o.AllowImageInputs = true;
            o.MaxImagesPerRequest = 2;
            o.MaxImageBytesPerImage = 1_024 * 1_024;
            o.MaxImageBytesPerRequest = 2 * 1_024 * 1_024;
            o.EstimatedTokensPerImage = 500;
        }));

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("http://unit-test.invalid/v1/", "endpoint must be absolute https")]
    [InlineData("https://localhost/v1/", "localhost is not allowed")]
    [InlineData("https://unit-test.invalid:8443/v1/", "port must be 443")]
    [InlineData("", "model must be a non-empty approved model name")]
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
    public void Missing_endpoint_fails_validation()
    {
        var options = Approved(o => o.Endpoint = null);

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains("endpoint must be absolute https", string.Join("; ", result.Failures), StringComparison.Ordinal);
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

    [Fact]
    public void Endpoint_outside_the_approved_host_list_fails_validation()
    {
        var options = Approved(o => o.ApprovedHosts = ["api.example.com"]);

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains("approved host list", string.Join("; ", result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_below_output_token_limit_fails()
    {
        var options = Approved(o => o.MaxTotalTokensPerCall = o.MaxOutputTokens - 1);

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains("budget", string.Join("; ", result.Failures), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70_000)]
    public void Invalid_approved_port_fails(int port)
    {
        var options = Approved(o => o.ApprovedPort = port);

        var result = Validate(options);

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("modulo\ncom-quebra")]
    public void Invalid_module_tag_fails(string moduleTag)
    {
        var options = Approved(o => o.ModuleTag = moduleTag);

        var result = Validate(options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Vision_caps_outside_the_approved_ranges_fail()
    {
        var result = Validate(Approved(o =>
        {
            o.AllowImageInputs = true;
            o.MaxImagesPerRequest = 99;
        }));

        Assert.True(result.Failed);
        Assert.Contains("image count limit", string.Join("; ", result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Vision_admission_estimate_above_the_call_budget_fails()
    {
        var result = Validate(Approved(o =>
        {
            o.AllowImageInputs = true;
            o.MaxImagesPerRequest = 16;
            o.EstimatedTokensPerImage = 10_000;
        }));

        Assert.True(result.Failed);
        Assert.Contains("image admission estimate", string.Join("; ", result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Null_options_fails_validation()
    {
        var result = new OpenAiCompatibleOptions.Validator().Validate(nameof(OpenAiCompatibleOptions), null!);

        Assert.True(result.Failed);
    }
}
