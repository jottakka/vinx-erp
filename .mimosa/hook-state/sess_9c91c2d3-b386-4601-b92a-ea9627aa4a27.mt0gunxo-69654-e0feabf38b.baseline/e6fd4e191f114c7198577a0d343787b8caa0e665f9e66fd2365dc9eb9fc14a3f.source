namespace Vinx.Erp.Foundation.Domain;

public interface ICompanyScoped
{
    Guid CompanyId { get; }
}

public interface IConcurrencyVersioned
{
    long RowVersion { get; }
}

public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; }
    Guid? DeletedByUserId { get; }
    string? DeletedReason { get; }
}

public abstract class EntityBase
{
    public Guid Id { get; protected set; }
}

public abstract class AuditableEntity : EntityBase
{
    public Guid CreatedByUserId { get; protected set; }
    public Guid UpdatedByUserId { get; protected set; }
    public Guid RequestId { get; protected set; }
    public DateTimeOffset CreatedAt { get; protected set; }
    public DateTimeOffset UpdatedAt { get; protected set; }

    protected void InitializeAudit(Guid id, Guid actorUserId, Guid requestId)
    {
        Id = id;
        CreatedByUserId = actorUserId;
        UpdatedByUserId = actorUserId;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        RequestId = requestId;
    }

    protected void InitializeAudit(Guid id, Guid actorUserId, Guid requestId, DateTimeOffset createdAt)
    {
        Id = id;
        CreatedByUserId = actorUserId;
        UpdatedByUserId = actorUserId;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        RequestId = requestId;
    }

    protected void RecordUpdate(Guid actorUserId, Guid requestId)
    {
        UpdatedByUserId = actorUserId;
        UpdatedAt = DateTimeOffset.UtcNow;
        RequestId = requestId;
    }
}

public abstract class SoftDeletableEntity : AuditableEntity, ISoftDeletable, IConcurrencyVersioned
{
    public DateTimeOffset? DeletedAt { get; protected set; }
    public Guid? DeletedByUserId { get; protected set; }
    public string? DeletedReason { get; protected set; }
    public long RowVersion { get; protected set; }

    protected void InitializeLifecycle(Guid id, Guid actorUserId, Guid requestId)
    {
        InitializeAudit(id, actorUserId, requestId);
        RowVersion = 1;
    }

    protected void InitializeLifecycle(Guid id, Guid actorUserId, Guid requestId, DateTimeOffset createdAt)
    {
        InitializeAudit(id, actorUserId, requestId, createdAt);
        RowVersion = 1;
    }
}
