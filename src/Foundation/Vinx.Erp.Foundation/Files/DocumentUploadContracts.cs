using Microsoft.AspNetCore.Http;

namespace Vinx.Erp.Foundation.Files;

public sealed record CreateDocumentUploadIntentCommand(
    Guid CompanyId,
    Guid RequestId,
    string FileName,
    string MimeType,
    long SizeBytes,
    string Checksum,
    string Classification,
    string Origin,
    DateTimeOffset RetentionUntil);

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
