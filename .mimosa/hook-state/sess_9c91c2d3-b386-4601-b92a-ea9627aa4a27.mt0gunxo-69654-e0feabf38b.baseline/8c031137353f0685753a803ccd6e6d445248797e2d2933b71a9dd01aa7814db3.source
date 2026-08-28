namespace Vinx.Erp.Foundation.Domain;

public sealed class Membership : SoftDeletableEntity, ICompanyScoped
{
    private Membership()
    {
    }

    public Membership(Guid id, Guid companyId, Guid userId, Guid actorUserId, Guid requestId)
    {
        CompanyId = companyId;
        UserId = userId;
        InitializeLifecycle(id, actorUserId, requestId);
    }

    public Guid CompanyId { get; private set; }
    public Guid UserId { get; private set; }

    public Company Company { get; private set; } = null!;
    public User User { get; private set; } = null!;

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
