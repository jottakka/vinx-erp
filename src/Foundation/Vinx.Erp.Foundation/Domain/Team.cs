namespace Vinx.Erp.Foundation.Domain;

public sealed class Team
{
    private Team()
    {
    }

    public Team(Guid id, Guid companyId, Guid unitId, string name, Guid actorUserId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        CompanyId = companyId;
        UnitId = unitId;
        Name = name;
        CreatedByUserId = actorUserId;
        UpdatedByUserId = actorUserId;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        RequestId = requestId;
        RowVersion = 1;
    }

    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid UnitId { get; private set; }
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

    public Company Company { get; private set; } = null!;
    public Unit Unit { get; private set; } = null!;
}
