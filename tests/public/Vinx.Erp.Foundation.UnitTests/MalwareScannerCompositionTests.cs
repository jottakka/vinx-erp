using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.Files.ClamAv;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class MalwareScannerCompositionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Select_without_endpoint_returns_the_blocked_scanner(string? endpoint)
    {
        var options = new MalwareScannerOptions { ClamAvEndpoint = endpoint };

        var scanner = MalwareScannerComposition.SelectScanner(options);

        Assert.IsType<BlockedMalwareScanner>(scanner);
    }

    [Fact]
    public void Select_with_configured_endpoint_returns_the_clamav_scanner()
    {
        var options = new MalwareScannerOptions { ClamAvEndpoint = "localhost:3310" };

        var scanner = MalwareScannerComposition.SelectScanner(options);

        Assert.IsType<ClamAvMalwareScanner>(scanner);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:notaport")]
    public void Select_with_malformed_endpoint_throws_argument_exception(string endpoint)
    {
        var options = new MalwareScannerOptions { ClamAvEndpoint = endpoint };

        Assert.Throws<ArgumentException>(() => MalwareScannerComposition.SelectScanner(options));
    }

    [Fact]
    public void Select_rejects_null_options()
    {
        Assert.Throws<ArgumentNullException>(
            () => MalwareScannerComposition.SelectScanner(null!));
    }
}
