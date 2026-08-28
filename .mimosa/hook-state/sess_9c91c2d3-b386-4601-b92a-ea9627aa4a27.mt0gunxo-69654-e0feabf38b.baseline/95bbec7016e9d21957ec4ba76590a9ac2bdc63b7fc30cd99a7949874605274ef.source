namespace Vinx.Erp.Foundation.Domain;

public sealed class Group : SoftDeletableEntity
{
    private Group()
    {
    }

    public Group(Guid id, string name, Guid actorUserId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        InitializeLifecycle(id, actorUserId, requestId);
    }

    public string Name { get; private set; } = null!;

    public void Rename(string name, Guid actorUserId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        RecordUpdate(actorUserId, requestId);
    }
}
