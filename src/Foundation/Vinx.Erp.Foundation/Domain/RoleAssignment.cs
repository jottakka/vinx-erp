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

    /// <summary>Soft-delete idempotente: atribuição já revogada permanece inalterada.</summary>
    public void Revoke(string reason, Guid actorUserId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (DeletedAt is not null)
        {
            return;
        }

        DeletedAt = DateTimeOffset.UtcNow;
        DeletedByUserId = actorUserId;
        DeletedReason = reason;
        RecordUpdate(actorUserId, requestId);
    }
}
