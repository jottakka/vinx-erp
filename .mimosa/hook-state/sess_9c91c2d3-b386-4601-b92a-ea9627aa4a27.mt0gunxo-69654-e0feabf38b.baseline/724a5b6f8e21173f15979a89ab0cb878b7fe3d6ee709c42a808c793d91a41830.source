namespace Vinx.Erp.Foundation.Domain;

public sealed class RoleAssignment : SoftDeletableEntity, ICompanyScoped
{
    private RoleAssignment()
    {
    }

    public RoleAssignment(Guid id, Guid companyId, Guid roleId, Guid membershipId, Guid actorUserId, Guid requestId)
    {
        CompanyId = companyId;
        RoleId = roleId;
        MembershipId = membershipId;
        InitializeLifecycle(id, actorUserId, requestId);
    }

    public Guid CompanyId { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid MembershipId { get; private set; }

    public Company Company { get; private set; } = null!;
    public Role Role { get; private set; } = null!;
    public Membership Membership { get; private set; } = null!;
}
