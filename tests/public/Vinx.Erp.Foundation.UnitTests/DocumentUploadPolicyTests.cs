using Vinx.Erp.Foundation.Files;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class DocumentUploadPolicyTests
{
    private const string Checksum = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("invoice.pdf", "application/pdf", 128L, Checksum, null)]
    [InlineData("photo.jpg", "image/jpeg", 128L, Checksum, null)]
    [InlineData("", "application/pdf", 128L, Checksum, "file_name_required")]
    [InlineData("invoice.pdf", "image/png", 128L, Checksum, "mime_extension_mismatch")]
    [InlineData("invoice.pdf", "application/pdf", 0L, Checksum, "empty_file")]
    [InlineData("invoice.pdf", "application/pdf", 26214401L, Checksum, "file_too_large")]
    [InlineData("invoice.pdf", "application/pdf", 128L, "sha256:invalid", "checksum_invalid")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.pdf", "application/pdf", 128L, Checksum, "file_name_too_long")]
    [InlineData("invoice.abcdefghijklmnopqrstu", "application/pdf", 128L, Checksum, "extension_too_long")]
    [InlineData("invoice.pdf", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 128L, Checksum, "mime_type_too_long")]
    public void Upload_policy_accepts_only_supported_file_metadata(
        string fileName,
        string mimeType,
        long sizeBytes,
        string checksum,
        string? expectedError)
    {
        Assert.Equal(expectedError, DocumentUploadPolicy.Validate(fileName, mimeType, sizeBytes, checksum));
    }

    [Fact]
    public void Upload_policy_rejects_checksum_longer_than_the_mapped_column()
    {
        var checksum = $"sha256:{new string('a', 122)}";

        Assert.Equal("checksum_too_long", DocumentUploadPolicy.Validate("invoice.pdf", "application/pdf", 128L, checksum));
    }
}
