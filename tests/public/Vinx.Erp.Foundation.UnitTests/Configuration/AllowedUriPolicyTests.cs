using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Vinx.Erp.Foundation.Configuration;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class AllowedUriPolicyTests
{
    [Theory]
    [InlineData("https://auth.example.test", true)]
    [InlineData("https://auth.example.test/auth/v1", true)]
    [InlineData("https://example.test/path?query=1", true)]
    [InlineData("http://127.0.0.1:54321/auth/v1", true)]
    [InlineData("http://localhost:54321/auth/v1", true)]
    [InlineData("http://[::1]:54321", true)]
    [InlineData("http://192.168.0.10:54321", false)]
    [InlineData("http://example.test", false)]
    [InlineData("https://user:pass@example.test", false)]
    [InlineData("https://example.test/#fragment", false)]
    [InlineData("ftp://example.test", false)]
    [InlineData("/relative/path", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void IsAllowedHttp_accepts_https_and_dev_loopback_only(string value, bool expected)
    {
        Assert.Equal(expected, AllowedUriPolicy.IsAllowedHttp(value, Host("Development")));
    }

    [Theory]
    [InlineData("https://auth.example.test", true)]
    [InlineData("http://127.0.0.1:54321", false)]
    [InlineData("http://localhost:54321", false)]
    public void IsAllowedHttp_rejects_http_outside_development(string value, bool expected)
    {
        Assert.Equal(expected, AllowedUriPolicy.IsAllowedHttp(value, Host("Production")));
    }

    [Theory]
    [InlineData("https://app.example.test", true)]
    [InlineData("http://127.0.0.1:54321", false)]
    [InlineData("http://localhost:54321", false)]
    [InlineData("https://user:pass@app.example.test", false)]
    [InlineData("https://app.example.test/#fragment", false)]
    [InlineData("ftp://app.example.test", false)]
    [InlineData("app.example.test", false)]
    [InlineData("", false)]
    public void IsAllowedHttps_has_no_loopback_exception(string value, bool expected)
    {
        Assert.Equal(expected, AllowedUriPolicy.IsAllowedHttps(value));
    }

    private static IHostEnvironment Host(string environmentName) =>
        new TestHostEnvironment(environmentName);

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "unit-tests";

        public string EnvironmentName { get; set; } = environmentName;

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
