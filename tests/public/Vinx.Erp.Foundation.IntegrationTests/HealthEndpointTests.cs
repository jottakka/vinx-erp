using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task GetHealth_ReturnsHealthy()
    {
        await using var app = new WebApplicationFactory<Program>();
        var response = await app.CreateClient().GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
