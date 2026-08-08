namespace Vinx.Erp.Foundation.Domain;

public sealed class Company
{
    private Company()
    {
    }

    public Company(Guid id, Guid groupId, string name, Guid actorUserId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        GroupId = groupId;
        Name = name;
        CreatedByUserId = actorUserId;
        UpdatedByUserId = actorUserId;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        RequestId = requestId;
        RowVersion = 1;
    }

    public Guid Id { get; private set; }
    public Guid GroupId { get; private set; }
    public string Name { get; private set; } = null!;
    public Guid CreatedByUserId { get; private set; }
    public Guid UpdatedByUserId { get; private set; }
    public Guid RequestId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedByUserId { get; private set; }
    public string? DeletedReason { get; private set; }
    public long RowVersion { get; private set; }

    public Group Group { get; private set; } = null!;

    public void Rename(string name, Guid actorUserId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        UpdatedByUserId = actorUserId;
        UpdatedAt = DateTimeOffset.UtcNow;
        RequestId = requestId;
    }
}
