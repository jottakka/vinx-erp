using System.Net;
using Xunit;
using Vinx.Erp.AI.Providers.OpenAiCompatible;

namespace Vinx.Erp.AI.UnitTests.Providers.OpenAiCompatible;

public sealed class OpenAiCompatibleTransportTests
{
    [Fact]
    public void Create_disables_redirects_proxy_and_cookies_for_an_approved_profile()
    {
        using var handler = OpenAiCompatibleTransport.Create(Approved());

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Theory]
    [InlineData("http://api.example.test/v1/")]
    [InlineData("https://api.example.test:8443/v1/")]
    public void Create_rejects_endpoints_outside_https_443(string endpoint)
    {
        var profile = Approved(o => o.Endpoint = new Uri(endpoint, UriKind.Absolute));

        Assert.Throws<ProviderEgressRejectedException>(() => OpenAiCompatibleTransport.Create(profile));
    }

    [Fact]
    public void Create_enforces_the_exact_or_dot_boundary_host_allowlist()
    {
        var profile = Approved(o =>
        {
            o.Endpoint = new Uri("https://not-api.example.test/v1/", UriKind.Absolute);
            o.ApprovedHosts = ["api.example.test"];
        });

        Assert.Throws<ProviderEgressRejectedException>(() => OpenAiCompatibleTransport.Create(profile));
    }

    [Fact]
    public async Task ResolveApprovedAddressesAsync_accepts_public_A_and_AAAA_records()
    {
        var addresses = await OpenAiCompatibleTransport.ResolveApprovedAddressesAsync(
            new Uri("https://api.example.test"),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2606:4700::1111") }),
            CancellationToken.None);

        Assert.Equal(2, addresses.Length);
    }

    [Fact]
    public async Task Transport_rejects_a_private_DNS_result_before_connecting()
    {
        await Assert.ThrowsAsync<ProviderEgressRejectedException>(() =>
            OpenAiCompatibleTransport.ResolveApprovedAddressesAsync(
                new Uri("https://api.example.test"),
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.2") }),
                CancellationToken.None));
    }

    [Fact]
    public async Task ResolveApprovedAddressesAsync_rejects_the_entire_result_when_any_record_is_prohibited()
    {
        await Assert.ThrowsAsync<ProviderEgressRejectedException>(() =>
            OpenAiCompatibleTransport.ResolveApprovedAddressesAsync(
                new Uri("https://api.example.test"),
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("fd00::1") }),
                CancellationToken.None));
    }

    [Fact]
    public async Task ResolveApprovedAddressesAsync_propagates_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OpenAiCompatibleTransport.ResolveApprovedAddressesAsync(
                new Uri("https://api.example.test"),
                async (_, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return [];
                },
                cancellation.Token));
    }

    private static OpenAiCompatibleOptions Approved(Action<OpenAiCompatibleOptions>? mutate = null)
    {
        var options = new OpenAiCompatibleOptions
        {
            Endpoint = new Uri("https://api.example.test/v1/", UriKind.Absolute),
            Model = "approved-model",
            ApiKey = "synthetic-test-key",
            ApprovedHosts = ["api.example.test"],
        };
        mutate?.Invoke(options);
        return options;
    }
}
