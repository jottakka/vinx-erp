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
        string checksum = Checksum,
        byte[]? leadingBytes = null)
    {
        // Por padrão os bytes iniciais batem com o MIME declarado (PDF de verdade);
        // os testes de mismatch passam bytes explícitos que não batem.
        leadingBytes ??= "%PDF-"u8.ToArray();
        return new(
            new StorageObjectMetadata(record.ProviderKey, sizeBytes, contentType, checksum),
            MalwareScanStatus.Clean,
            leadingBytes);
    }

    [Fact]
    public void Verification_rejects_binary_content_whose_leading_bytes_do_not_match_the_declared_type()
    {
        var record = NewRecord();
        var stored = NewInspection(record, leadingBytes: "safe document payload"u8.ToArray());

        Assert.Equal(
            "content_magic_mismatch",
            DocumentVerificationPolicy.Validate(record, stored, NewCleanVerdict(Checksum)));
    }

    [Fact]
    public void Verification_accepts_binary_content_whose_leading_bytes_match_the_signature()
    {
        var pngRecord = NewRecord("photo.png", ".png", "image/png");
        var pngStored = NewInspection(
            pngRecord, contentType: "image/png", leadingBytes: [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Assert.Null(DocumentVerificationPolicy.Validate(pngRecord, pngStored, NewCleanVerdict(Checksum)));

        var jpegRecord = NewRecord("photo.jpg", ".jpg", "image/jpeg");
        var jpegStored = NewInspection(
            jpegRecord, contentType: "image/jpeg", leadingBytes: [0xFF, 0xD8, 0xFF, 0xE1]);
        Assert.Null(DocumentVerificationPolicy.Validate(jpegRecord, jpegStored, NewCleanVerdict(Checksum)));
    }

    [Fact]
    public void Verification_skips_magic_sniffing_for_unsigned_text_content()
    {
        var record = NewRecord("notes.txt", ".txt", "text/plain");
        var stored = NewInspection(record, contentType: "text/plain", leadingBytes: []);

        Assert.Null(DocumentVerificationPolicy.Validate(record, stored, NewCleanVerdict(Checksum)));
    }

    private static MalwareScanVerdict NewCleanVerdict(string checksum) => new(
        MalwareScanStatus.Clean, "vinx-test-engine", "1.0.0", "1.2026.08", DateTimeOffset.UtcNow, checksum, null);

    private static FileRecord NewRecord(
        string fileName = "document.pdf",
        string extension = ".pdf",
        string mimeType = "application/pdf") => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), fileName, extension, 128L,
        mimeType, Checksum, "financial", "user_upload", DateTimeOffset.UtcNow.AddDays(30),
        Guid.NewGuid(), Guid.NewGuid());
}
