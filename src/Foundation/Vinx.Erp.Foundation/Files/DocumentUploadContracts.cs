using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations;

namespace Vinx.Erp.Foundation.Files;

public sealed record CreateDocumentUploadIntentCommand(
    Guid CompanyId,
    Guid RequestId,
    [property: Required(ErrorMessage = "file_name_required")]
    [property: StringLength(DocumentUploadPolicy.MaxFileNameLength, ErrorMessage = "file_name_too_long")]
    string FileName,
    [property: Required(ErrorMessage = "mime_required")]
    [property: StringLength(DocumentUploadPolicy.MaxMimeTypeLength, ErrorMessage = "mime_type_too_long")]
    string MimeType,
    long SizeBytes,
    [property: Required(ErrorMessage = "checksum_invalid")]
    [property: StringLength(DocumentUploadPolicy.MaxChecksumLength, ErrorMessage = "checksum_too_long")]
    string Checksum,
    [property: Required(ErrorMessage = "classification_required")]
    [property: StringLength(DocumentUploadPolicy.MaxClassificationLength, ErrorMessage = "classification_too_long")]
    string Classification,
    [property: Required(ErrorMessage = "origin_required")]
    [property: StringLength(DocumentUploadPolicy.MaxOriginLength, ErrorMessage = "origin_too_long")]
    string Origin,
    DateTimeOffset RetentionUntil);

public static class DocumentUploadIntentCommandValidation
{
    public static string? Validate(CreateDocumentUploadIntentCommand command)
    {
        var results = new List<ValidationResult>();
        return Validator.TryValidateObject(command, new ValidationContext(command), results, true)
            ? DocumentUploadPolicy.Validate(command.FileName, command.MimeType, command.SizeBytes, command.Checksum)
                ?? DocumentUploadPolicy.ValidateIntentMetadata(
                    command.Classification, command.Origin, command.RetentionUntil)
            : results[0].ErrorMessage;
    }
}

public sealed record ConfirmDocumentUploadCommand(Guid CompanyId, Guid RequestId, Guid FileId);

public sealed record DocumentUploadIntentResult(Guid FileId, string ProcessingState, Uri UploadUrl);

public sealed record DocumentUploadConfirmationResult(Guid FileId, string ProcessingState);

public sealed record DocumentUploadResult<T>(T? Value, string? ErrorCode, int? StatusCode)
{
    public bool IsSuccess => ErrorCode is null;

    public static DocumentUploadResult<T> Success(T value) => new(value, null, null);

    public static DocumentUploadResult<T> Failure(string errorCode, int statusCode) => new(default, errorCode, statusCode);

    public IResult ToMinimalApiResult() => IsSuccess
        ? Results.Ok(Value)
        : Results.Json(new DocumentUploadErrorResponse(ErrorCode!), statusCode: StatusCode);
}

public sealed record DocumentUploadErrorResponse(string Code);
