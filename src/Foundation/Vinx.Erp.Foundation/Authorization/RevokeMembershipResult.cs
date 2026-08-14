namespace Vinx.Erp.Foundation.Authorization;

public sealed record RevokeMembershipResult(Guid MembershipId, bool WasRevoked);
