using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task GetHealth_ReturnsHealthy()
    {
        await using var app = await FoundationApiFactory.CreateAsync();
        var response = await app.CreateClient().GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task GetHealthLive_ReturnsHealthy_WithoutRunningChecks()
    {
        await using var app = await FoundationApiFactory.CreateAsync();
        var response = await app.CreateClient().GetAsync("/health/live");

        response.EnsureSuccessStatusCode();
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task GetHealthReady_RunsThePostgresCheck()
    {
        await using var app = await FoundationApiFactory.CreateAsync();
        var response = await app.CreateClient().GetAsync("/health/ready");

        response.EnsureSuccessStatusCode();
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
