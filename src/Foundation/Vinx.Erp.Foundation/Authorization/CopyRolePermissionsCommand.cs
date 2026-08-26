namespace Vinx.Erp.Foundation.Authorization;

public sealed record CopyRolePermissionsCommand(
    Guid CompanyId,
    Guid SourceMembershipId,
    Guid TargetMembershipId,
    Guid RequestId);
