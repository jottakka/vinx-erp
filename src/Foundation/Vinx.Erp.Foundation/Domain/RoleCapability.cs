namespace Vinx.Erp.Foundation.Domain;

public sealed class RoleCapability
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
        Id = id;
        CompanyId = companyId;
        RoleId = roleId;
        CapabilityId = capabilityId;
        Scope = scope;
        CreatedByUserId = actorUserId;
        UpdatedByUserId = actorUserId;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        RequestId = requestId;
        RowVersion = 1;
    }

    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid CapabilityId { get; private set; }
    public CapabilityScope Scope { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public Guid UpdatedByUserId { get; private set; }
    public Guid RequestId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedByUserId { get; private set; }
    public string? DeletedReason { get; private set; }
    public long RowVersion { get; private set; }

    public Role Role { get; private set; } = null!;
    public Capability Capability { get; private set; } = null!;
}
