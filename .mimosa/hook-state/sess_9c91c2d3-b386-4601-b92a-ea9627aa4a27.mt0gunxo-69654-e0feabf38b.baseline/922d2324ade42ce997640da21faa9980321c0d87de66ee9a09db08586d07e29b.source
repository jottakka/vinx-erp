namespace Vinx.Erp.Foundation.Domain;

public sealed class RoleCapability : SoftDeletableEntity, ICompanyScoped
{
    private RoleCapability()
    {
    }

    public RoleCapability(
        Guid id,
        Guid companyId,
        Guid roleId,
        Guid capabilityId,
        CapabilityScope scope,
        Guid actorUserId,
        Guid requestId)
    {
        CompanyId = companyId;
        RoleId = roleId;
        CapabilityId = capabilityId;
        Scope = scope;
        InitializeLifecycle(id, actorUserId, requestId);
    }

    public Guid CompanyId { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid CapabilityId { get; private set; }
    public CapabilityScope Scope { get; private set; }

    public Role Role { get; private set; } = null!;
    public Capability Capability { get; private set; } = null!;
}
