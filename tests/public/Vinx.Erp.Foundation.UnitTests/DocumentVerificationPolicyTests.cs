using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Files;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class DocumentVerificationPolicyTests
{
    private const string Checksum = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Verification_accepts_matching_object_metadata()
    {
        var record = NewRecord();
        var stored = new StorageObjectMetadata(record.ProviderKey, 128L, "application/pdf", Checksum);

        Assert.Null(DocumentVerificationPolicy.Validate(record, stored, malwareDetected: false));
    }

    [Fact]
    public void Verification_rejects_metadata_for_a_different_storage_key_before_other_checks()
    {
        var record = NewRecord();
        var stored = new StorageObjectMetadata(
            "quarantine/other/document",
            127L,
            "image/png",
            "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        Assert.Equal("object_key_mismatch", DocumentVerificationPolicy.Validate(record, stored, malwareDetected: false));
    }

    [Theory]
    [InlineData("size_mismatch", 127L, "application/pdf", Checksum, false)]
    [InlineData("checksum_mismatch", 128L, "application/pdf", "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", false)]
    [InlineData("content_type_mismatch", 128L, "image/png", Checksum, false)]
    [InlineData("malware_detected", 128L, "application/pdf", Checksum, true)]
    public void Verification_rejects_invalid_or_unsafe_object_metadata(
        string expectedError,
        long sizeBytes,
        string contentType,
        string checksum,
        bool malwareDetected)
    {
        var record = NewRecord();
        var stored = new StorageObjectMetadata(record.ProviderKey, sizeBytes, contentType, checksum);

        Assert.Equal(expectedError, DocumentVerificationPolicy.Validate(record, stored, malwareDetected));
    }

    private static FileRecord NewRecord() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "document.pdf", ".pdf", 128L,
        "application/pdf", Checksum, "financial", "user_upload", DateTimeOffset.UtcNow.AddDays(30),
        Guid.NewGuid(), Guid.NewGuid());
}
