using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Authorization;

public sealed class CapabilityEvaluator(FoundationDbContext database)
{
    public Task<bool> HasGrantAsync(
        Guid companyId,
        Guid membershipId,
        CapabilityRequirement requirement,
        CancellationToken cancellationToken) =>
        (from assignment in database.RoleAssignments.AsNoTracking()
         join role in database.Roles.AsNoTracking() on assignment.RoleId equals role.Id
         join grant in database.RoleCapabilities.AsNoTracking() on role.Id equals grant.RoleId
         join capability in database.Capabilities.AsNoTracking() on grant.CapabilityId equals capability.Id
         where assignment.CompanyId == companyId
             && assignment.MembershipId == membershipId
             && role.CompanyId == companyId
             && grant.CompanyId == companyId
             && capability.Name == requirement.Name
             && grant.Scope == requirement.RequiredScope
         select assignment.Id).AnyAsync(cancellationToken);
}
