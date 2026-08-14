namespace Vinx.Erp.Foundation.Authorization;

public sealed record RevokeMembershipCommand(Guid CompanyId, Guid MembershipId, Guid RequestId);
