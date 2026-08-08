namespace Vinx.Erp.Foundation.Domain;

public sealed class RoleAssignment
{
    private RoleAssignment()
    {
    }

    public RoleAssignment(Guid id, Guid companyId, Guid roleId, Guid membershipId, Guid actorUserId, Guid requestId)
    {
        Id = id;
        CompanyId = companyId;
        RoleId = roleId;
        MembershipId = membershipId;
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
    public Guid MembershipId { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public Guid UpdatedByUserId { get; private set; }
    public Guid RequestId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedByUserId { get; private set; }
    public string? DeletedReason { get; private set; }
    public long RowVersion { get; private set; }

    public Company Company { get; private set; } = null!;
    public Role Role { get; private set; } = null!;
    public Membership Membership { get; private set; } = null!;
}
