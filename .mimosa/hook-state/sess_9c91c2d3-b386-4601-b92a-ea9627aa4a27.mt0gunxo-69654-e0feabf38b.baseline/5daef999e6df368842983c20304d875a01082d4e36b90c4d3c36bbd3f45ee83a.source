namespace Vinx.Erp.Foundation.Domain;

public sealed class Company : SoftDeletableEntity
{
    private Company()
    {
    }

    public Company(Guid id, Guid groupId, string name, Guid actorUserId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        GroupId = groupId;
        Name = name;
        InitializeLifecycle(id, actorUserId, requestId);
    }

    public Guid GroupId { get; private set; }
    public string Name { get; private set; } = null!;

    public Group Group { get; private set; } = null!;

    public void Rename(string name, Guid actorUserId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        RecordUpdate(actorUserId, requestId);
    }
}
