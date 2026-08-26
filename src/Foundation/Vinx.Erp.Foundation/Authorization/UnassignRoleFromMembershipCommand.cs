namespace Vinx.Erp.Foundation.Authorization;

public sealed record UnassignRoleFromMembershipCommand(
    Guid CompanyId,
    Guid RoleId,
    Guid MembershipId,
    Guid RequestId);
