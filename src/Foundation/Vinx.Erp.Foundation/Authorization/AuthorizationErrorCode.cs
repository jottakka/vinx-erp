namespace Vinx.Erp.Foundation.Authorization;

public enum AuthorizationErrorCode
{
    CompanyContextRequired,
    MembershipRequired,
    CapabilityDenied,
    ResourceNotFound,
    IdempotencyConflict,
    AuditUnavailable
}
