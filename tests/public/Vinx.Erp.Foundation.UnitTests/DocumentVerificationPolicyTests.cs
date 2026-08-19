using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Files;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class DocumentVerificationPolicyTests
{
    private const string Checksum = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherChecksum = "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    [Fact]
    public void Inspection_contract_exposes_an_antimalware_status()
    {
        var property = typeof(StorageObjectInspection).GetProperty("MalwareScanStatus");

        Assert.NotNull(property);
        Assert.Equal("MalwareScanStatus", property!.PropertyType.Name);
    }

    [Fact]
    public void Verification_accepts_current_clean_verdict()
    {
        var record = NewRecord();
        var stored = NewInspection(record);

        Assert.Null(DocumentVerificationPolicy.Validate(record, stored, NewCleanVerdict(Checksum)));
        Assert.Null(DocumentVerificationPolicy.Validate(record, stored, NewCleanVerdict(Checksum.ToUpperInvariant())));
    }

    [Fact]
    public void Verification_rejects_malware_detected_verdict_before_staleness_checks()
    {
        var record = NewRecord();
        var stored = NewInspection(record);

        var verdict = new MalwareScanVerdict(
            MalwareScanStatus.MalwareDetected, null, null, null, default, null, "evidence_matched");

        Assert.Equal("malware_detected", DocumentVerificationPolicy.Validate(record, stored, verdict));
    }

    [Fact]
    public void Verification_rejects_unavailable_verdict()
    {
        var record = NewRecord();
        var stored = NewInspection(record);

        var verdict = new MalwareScanVerdict(
            MalwareScanStatus.Unavailable, null, null, null, DateTimeOffset.UtcNow, null, "scanner_not_configured");

        Assert.Equal("malware_scan_unavailable", DocumentVerificationPolicy.Validate(record, stored, verdict));
    }

    [Theory]
    [InlineData(false, true, true, true, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, true, true, false)]
    public void Verification_rejects_malformed_clean_verdict(
        bool hasEngine,
        bool hasEngineVersion,
        bool hasSignatureVersion,
        bool hasContentChecksum,
        bool hasScannedAt)
    {
        var record = NewRecord();
        var stored = NewInspection(record);
        var verdict = new MalwareScanVerdict(
            MalwareScanStatus.Clean,
            hasEngine ? "vinx-test-engine" : null,
            hasEngineVersion ? "1.0.0" : null,
            hasSignatureVersion ? "1.2026.08" : null,
            hasScannedAt ? DateTimeOffset.UtcNow : default,
            hasContentChecksum ? Checksum : null,
            null);

        Assert.Equal("malware_scan_unavailable", DocumentVerificationPolicy.Validate(record, stored, verdict));
    }

    [Fact]
    public void Verification_rejects_stale_verdict_for_changed_object()
    {
        var record = NewRecord();
        var stored = NewInspection(record);

        Assert.Equal(
            "malware_scan_unavailable",
            DocumentVerificationPolicy.Validate(record, stored, NewCleanVerdict(OtherChecksum)));
    }

    [Theory]
    [InlineData(default(MalwareScanStatus))]
    [InlineData((MalwareScanStatus)int.MaxValue)]
    public void Verification_fails_closed_for_unknown_verdict_statuses(MalwareScanStatus verdictStatus)
    {
        var record = NewRecord();
        var stored = NewInspection(record);

        var verdict = new MalwareScanVerdict(
            verdictStatus, "vinx-test-engine", "1.0.0", "1.2026.08", DateTimeOffset.UtcNow, Checksum, null);

        Assert.Equal("malware_scan_unavailable", DocumentVerificationPolicy.Validate(record, stored, verdict));
    }

    [Fact]
    public void Verification_rejects_metadata_for_a_different_storage_key_before_verdict_checks()
    {
        var record = NewRecord();
        var stored = new StorageObjectInspection(
            new StorageObjectMetadata("quarantine/other/document", 127L, "image/png", OtherChecksum),
            MalwareScanStatus.Clean);

        Assert.Equal(
            "object_key_mismatch",
            DocumentVerificationPolicy.Validate(
                record, stored, NewCleanVerdict(OtherChecksum) with { Status = MalwareScanStatus.MalwareDetected }));
    }

    [Theory]
    [InlineData("size_mismatch", 127L, "application/pdf", Checksum)]
    [InlineData("checksum_mismatch", 128L, "application/pdf", OtherChecksum)]
    [InlineData("content_type_mismatch", 128L, "image/png", Checksum)]
    public void Verification_rejects_invalid_object_metadata_before_the_verdict(
        string expectedError,
        long sizeBytes,
        string contentType,
        string checksum)
    {
        var record = NewRecord();
        var stored = NewInspection(record, sizeBytes, contentType, checksum);

        Assert.Equal(expectedError, DocumentVerificationPolicy.Validate(record, stored, NewCleanVerdict(checksum)));
    }

    [Fact]
    public async Task Verification_fails_closed_for_the_blocked_scanner_verdict()
    {
        var record = NewRecord();
        var stored = NewInspection(record);
        var verdict = await new BlockedMalwareScanner().ScanAsync(
            new MalwareScanRequest(record.ProviderKey, null), CancellationToken.None);

        Assert.Equal("malware_scan_unavailable", DocumentVerificationPolicy.Validate(record, stored, verdict));
    }

    // Retry após veredito indisponível é coberto na integração (estado na quarentena + novo scan),
    // onde a máquina de estados vive — a política é stateless por chamada.

    private static StorageObjectInspection NewInspection(
        FileRecord record,
        long sizeBytes = 128L,
        string contentType = "application/pdf",
        string checksum = Checksum) =>
        new(
            new StorageObjectMetadata(record.ProviderKey, sizeBytes, contentType, checksum),
            MalwareScanStatus.Clean);

    private static MalwareScanVerdict NewCleanVerdict(string checksum) => new(
        MalwareScanStatus.Clean, "vinx-test-engine", "1.0.0", "1.2026.08", DateTimeOffset.UtcNow, checksum, null);

    private static FileRecord NewRecord() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "document.pdf", ".pdf", 128L,
        "application/pdf", Checksum, "financial", "user_upload", DateTimeOffset.UtcNow.AddDays(30),
        Guid.NewGuid(), Guid.NewGuid());
}
