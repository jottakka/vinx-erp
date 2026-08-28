namespace Vinx.Erp.Foundation.Domain;

public sealed class Employee : SoftDeletableEntity, ICompanyScoped
{
    private Employee()
    {
    }

    public Employee(Guid id, Guid companyId, Guid membershipId, string displayName, Guid actorUserId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        CompanyId = companyId;
        MembershipId = membershipId;
        DisplayName = displayName;
        InitializeLifecycle(id, actorUserId, requestId);
    }

    public Guid CompanyId { get; private set; }
    public Guid MembershipId { get; private set; }
    public string DisplayName { get; private set; } = null!;

    public Company Company { get; private set; } = null!;
    public Membership Membership { get; private set; } = null!;
}
