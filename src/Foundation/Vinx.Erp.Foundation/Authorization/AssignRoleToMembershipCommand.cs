namespace Vinx.Erp.Foundation.Authorization;

public sealed record AssignRoleToMembershipCommand(Guid CompanyId, Guid RoleId, Guid MembershipId, Guid RequestId);
