using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Authorization;

/// <summary>
/// Consultas do bloqueio de último administrador: impedem mutação que deixe a
/// empresa sem nenhum membro ativo com grant de
/// <c>foundation.roles.assign</c> em escopo company. As linhas soft-deletadas
/// já são excluídas pelos query filters globais.
/// </summary>
internal static class LastAdminGuard
{
    /// <summary>
    /// Indica se alguma outra membresia ativa da empresa (excluída a informada)
    /// mantém grant de atribuição de papéis em escopo company.
    /// </summary>
    public static Task<bool> CompanyRetainsRolesAssignAdminAsync(
        FoundationDbContext database,
        Guid companyId,
        Guid excludeMembershipId,
        CancellationToken cancellationToken) =>
        (from membership in database.Memberships.AsNoTracking()
         join assignment in database.RoleAssignments.AsNoTracking()
             on membership.Id equals assignment.MembershipId
         join grant in database.RoleCapabilities.AsNoTracking()
             on assignment.RoleId equals grant.RoleId
         join capability in database.Capabilities.AsNoTracking()
             on grant.CapabilityId equals capability.Id
         where membership.CompanyId == companyId
             && membership.Id != excludeMembershipId
             && assignment.CompanyId == companyId
             && grant.CompanyId == companyId
             && grant.Scope == CapabilityScope.Company
             && capability.Name == KnownCapabilities.RolesAssign
         select membership.Id).AnyAsync(cancellationToken);

    /// <summary>
    /// Indica se a membership informada mantém o grant por outras atribuições vivas
    /// (por exemplo, após excluir a atribuição informada ou ao receber o conjunto
    /// de papéis da fonte de uma cópia).
    /// </summary>
    public static Task<bool> MembershipRetainsRolesAssignAdminAsync(
        FoundationDbContext database,
        Guid companyId,
        Guid membershipId,
        Guid? excludeAssignmentId,
        CancellationToken cancellationToken) =>
        (from assignment in database.RoleAssignments.AsNoTracking()
         join grant in database.RoleCapabilities.AsNoTracking()
             on assignment.RoleId equals grant.RoleId
         join capability in database.Capabilities.AsNoTracking()
             on grant.CapabilityId equals capability.Id
         where assignment.CompanyId == companyId
             && assignment.MembershipId == membershipId
             && (excludeAssignmentId == null || assignment.Id != excludeAssignmentId)
             && grant.CompanyId == companyId
             && grant.Scope == CapabilityScope.Company
             && capability.Name == KnownCapabilities.RolesAssign
         select assignment.Id).AnyAsync(cancellationToken);
}
