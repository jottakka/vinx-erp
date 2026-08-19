using Vinx.Erp.Foundation.Domain;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class FileRecordScanMetadataTests
{
    private static readonly DateTimeOffset ScannedAtUtc = DateTimeOffset.UtcNow;

    [Fact]
    public void RecordScan_populates_metadata_only_via_method()
    {
        var record = NewRecord();

        // As propriedades têm private set: nascem vazias e só RecordScan as popula.
        Assert.Null(record.ScanEngine);
        Assert.Null(record.ScanEngineVersion);
        Assert.Null(record.ScanSignatureVersion);
        Assert.Null(record.ScannedAtUtc);

        record.RecordScan("ClamAV", "1.4.3", "daily/27000", ScannedAtUtc);

        Assert.Equal("ClamAV", record.ScanEngine);
        Assert.Equal("1.4.3", record.ScanEngineVersion);
        Assert.Equal("daily/27000", record.ScanSignatureVersion);
        Assert.Equal(ScannedAtUtc, record.ScannedAtUtc);
    }

    public static TheoryData<string?, string?, string?, string> IncompleteScanMetadata => new()
    {
        { null, "1.4.3", "daily/27000", "engine" },
        { "", "1.4.3", "daily/27000", "engine" },
        { "   ", "1.4.3", "daily/27000", "engine" },
        { "ClamAV", null, "daily/27000", "engineVersion" },
        { "ClamAV", "", "daily/27000", "engineVersion" },
        { "ClamAV", "   ", "daily/27000", "engineVersion" },
        { "ClamAV", "1.4.3", null, "signatureVersion" },
        { "ClamAV", "1.4.3", "", "signatureVersion" },
        { "ClamAV", "1.4.3", "   ", "signatureVersion" }
    };

    [Theory]
    [MemberData(nameof(IncompleteScanMetadata))]
    public void RecordScan_requires_complete_metadata(
        string? engine,
        string? engineVersion,
        string? signatureVersion,
        string expectedParamName)
    {
        var record = NewRecord();

        // null vira ArgumentNullException e vazio/espaço vira ArgumentException — ambos ArgumentException.
        var exception = Assert.ThrowsAny<ArgumentException>(
            () => record.RecordScan(engine!, engineVersion!, signatureVersion!, ScannedAtUtc));

        Assert.Equal(expectedParamName, exception.ParamName);
        Assert.Null(record.ScanEngine);
        Assert.Null(record.ScanEngineVersion);
        Assert.Null(record.ScanSignatureVersion);
        Assert.Null(record.ScannedAtUtc);
    }

    [Fact]
    public void RecordScan_with_default_scan_timestamp_is_rejected()
    {
        var record = NewRecord();

        var exception = Assert.Throws<ArgumentException>(
            () => record.RecordScan("ClamAV", "1.4.3", "daily/27000", default));

        Assert.Equal("scannedAtUtc", exception.ParamName);
        Assert.Null(record.ScannedAtUtc);
    }

    [Fact]
    public void RecordScan_keeps_processing_state_unchanged()
    {
        var record = NewRecord();

        record.RecordScan("ClamAV", "1.4.3", "daily/27000", ScannedAtUtc);

        Assert.Equal("quarantined", record.ProcessingState);
        record.MarkVerified(Guid.NewGuid(), Guid.NewGuid());
        record.RecordScan("ClamAV", "1.4.3", "daily/27000", ScannedAtUtc.AddSeconds(1));
        Assert.Equal("verified", record.ProcessingState);
    }

    private static FileRecord NewRecord() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "invoice.pdf",
        ".pdf",
        128,
        "application/pdf",
        "sha256:abc",
        "financial",
        "user_upload",
        DateTimeOffset.UtcNow.AddDays(30),
        Guid.NewGuid(),
        Guid.NewGuid());
}
