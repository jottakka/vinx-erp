using Vinx.Erp.Foundation.Files;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class DocumentUploadContractValidationTests
{
    private const string Checksum = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(InvalidField.EmptyFileName, "file_name_required")]
    [InlineData(InvalidField.FileNameAboveLimit, "file_name_too_long")]
    [InlineData(InvalidField.EmptyMimeType, "mime_required")]
    [InlineData(InvalidField.MimeTypeAboveLimit, "mime_type_too_long")]
    [InlineData(InvalidField.EmptyChecksum, "checksum_invalid")]
    [InlineData(InvalidField.ChecksumAboveLimit, "checksum_too_long")]
    [InlineData(InvalidField.EmptyClassification, "classification_required")]
    [InlineData(InvalidField.ClassificationAboveLimit, "classification_too_long")]
    [InlineData(InvalidField.EmptyOrigin, "origin_required")]
    [InlineData(InvalidField.OriginAboveLimit, "origin_too_long")]
    public void Contract_validation_returns_stable_code_for_each_invalid_structural_field(
        InvalidField invalidField,
        string expectedCode)
    {
        var command = invalidField switch
        {
            InvalidField.EmptyFileName => ValidIntent() with { FileName = string.Empty },
            InvalidField.FileNameAboveLimit => ValidIntent() with { FileName = new string('a', DocumentUploadPolicy.MaxFileNameLength + 1) },
            InvalidField.EmptyMimeType => ValidIntent() with { MimeType = string.Empty },
            InvalidField.MimeTypeAboveLimit => ValidIntent() with { MimeType = new string('a', DocumentUploadPolicy.MaxMimeTypeLength + 1) },
            InvalidField.EmptyChecksum => ValidIntent() with { Checksum = string.Empty },
            InvalidField.ChecksumAboveLimit => ValidIntent() with { Checksum = new string('a', DocumentUploadPolicy.MaxChecksumLength + 1) },
            InvalidField.EmptyClassification => ValidIntent() with { Classification = string.Empty },
            InvalidField.ClassificationAboveLimit => ValidIntent() with { Classification = new string('a', DocumentUploadPolicy.MaxClassificationLength + 1) },
            InvalidField.EmptyOrigin => ValidIntent() with { Origin = string.Empty },
            InvalidField.OriginAboveLimit => ValidIntent() with { Origin = new string('a', DocumentUploadPolicy.MaxOriginLength + 1) },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField), invalidField, null)
        };

        Assert.Equal(expectedCode, DocumentUploadIntentCommandValidation.Validate(command));
    }

    private static CreateDocumentUploadIntentCommand ValidIntent() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "invoice.pdf",
        "application/pdf",
        128,
        Checksum,
        "financial",
        "user_upload",
        DateTimeOffset.UtcNow.AddDays(1));

    public enum InvalidField
    {
        EmptyFileName,
        FileNameAboveLimit,
        EmptyMimeType,
        MimeTypeAboveLimit,
        EmptyChecksum,
        ChecksumAboveLimit,
        EmptyClassification,
        ClassificationAboveLimit,
        EmptyOrigin,
        OriginAboveLimit
    }
}
