using Microsoft.AspNetCore.Http;

namespace Vinx.Erp.Foundation.Authorization;

public sealed record AuthorizationResult<T>(T? Value, AuthorizationErrorCode? ErrorCode)
{
    public bool IsSuccess => ErrorCode is null;

    public static AuthorizationResult<T> Success(T value) => new(value, null);

    public static AuthorizationResult<T> Failure(AuthorizationErrorCode errorCode) => new(default, errorCode);

    public IResult ToMinimalApiResult()
    {
        if (IsSuccess)
        {
            return Results.Ok(Value);
        }

        return Results.Json(new AuthorizationErrorResponse(ErrorCode!.Value.ToStableCode()), statusCode: ErrorCode.Value.ToHttpStatusCode());
    }

    private sealed record AuthorizationErrorResponse(string Code);
}

public static class AuthorizationErrorCodeExtensions
{
    public static string ToStableCode(this AuthorizationErrorCode errorCode) => errorCode switch
    {
        AuthorizationErrorCode.CompanyContextRequired => "company_context_required",
        AuthorizationErrorCode.MembershipRequired => "membership_required",
        AuthorizationErrorCode.CapabilityDenied => "capability_denied",
        AuthorizationErrorCode.ResourceNotFound => "resource_not_found",
        AuthorizationErrorCode.IdempotencyConflict => "idempotency_conflict",
        AuthorizationErrorCode.AuditUnavailable => "audit_unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode, "Unsupported authorization error code.")
    };

    public static int ToHttpStatusCode(this AuthorizationErrorCode errorCode) => errorCode switch
    {
        AuthorizationErrorCode.CompanyContextRequired => StatusCodes.Status400BadRequest,
        AuthorizationErrorCode.MembershipRequired => StatusCodes.Status403Forbidden,
        AuthorizationErrorCode.CapabilityDenied => StatusCodes.Status403Forbidden,
        AuthorizationErrorCode.ResourceNotFound => StatusCodes.Status404NotFound,
        AuthorizationErrorCode.IdempotencyConflict => StatusCodes.Status409Conflict,
        AuthorizationErrorCode.AuditUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode, "Unsupported authorization error code.")
    };
}
