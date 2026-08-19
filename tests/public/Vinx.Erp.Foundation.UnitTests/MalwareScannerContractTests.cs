using Vinx.Erp.Foundation.Files;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class MalwareScannerContractTests
{
    [Fact]
    public async Task Blocked_scanner_always_returns_unavailable_with_safe_reason()
    {
        var scanner = new BlockedMalwareScanner();
        var openCalls = 0;
        var request = NewRequest(() => openCalls++);

        var verdict = await scanner.ScanAsync(request, CancellationToken.None);

        Assert.Equal(MalwareScanStatus.Unavailable, verdict.Status);
        Assert.Equal("scanner_not_configured", verdict.ReasonCode);
        Assert.Null(verdict.Engine);
        Assert.Null(verdict.EngineVersion);
        Assert.Null(verdict.SignatureVersion);
        Assert.Null(verdict.ContentChecksum);
        Assert.Equal(0, openCalls);
    }

    [Fact]
    public void Verdict_exposes_only_safe_properties()
    {
        var propertyNames = typeof(MalwareScanVerdict).GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "ContentChecksum",
                "Engine",
                "EngineVersion",
                "ReasonCode",
                "ScannedAtUtc",
                "SignatureVersion",
                "Status"
            },
            propertyNames);
    }

    [Fact]
    public void Options_reject_non_positive_timeout_and_limit()
    {
        var timeout = Assert.Throws<ArgumentException>(
            () => new MalwareScannerOptions { Timeout = TimeSpan.Zero }.Validate());

        Assert.Equal("Timeout", timeout.ParamName);

        var limit = Assert.Throws<ArgumentException>(
            () => new MalwareScannerOptions { MaxScanBytes = 0 }.Validate());

        Assert.Equal("MaxScanBytes", limit.ParamName);
    }

    [Fact]
    public void Options_defaults_are_conservative()
    {
        var options = new MalwareScannerOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.Equal(25 * 1024 * 1024, options.MaxScanBytes);
        Assert.Equal(DocumentUploadPolicy.MaxSizeBytes, options.MaxScanBytes);
    }

    [Fact]
    public void Request_opens_content_lazily()
    {
        var openCalls = 0;
        var request = NewRequest(() => openCalls++);

        Assert.Equal(0, openCalls);
    }

    private static MalwareScanRequest NewRequest(Action onOpen) => new(
        "quarantine/company-1/document-1.pdf",
        _ =>
        {
            onOpen();
            return Task.FromResult<Stream>(new MemoryStream());
        });
}
