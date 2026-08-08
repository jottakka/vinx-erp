using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PostgresContainerTests
{
    [Fact]
    public async Task ExecuteScalarAsync_ReturnsOne()
    {
        await using var fixture = new PostgresContainerFixture();

        var result = await fixture.ExecuteScalarAsync();

        Assert.Equal(1, result);
    }
}
