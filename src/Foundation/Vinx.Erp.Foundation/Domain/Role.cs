namespace Vinx.Erp.Foundation.Domain;

public sealed class Role : SoftDeletableEntity, ICompanyScoped
{
    private Role()
    {
    }

    public Role(Guid id, Guid companyId, string name, Guid actorUserId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        CompanyId = companyId;
        Name = name;
        InitializeLifecycle(id, actorUserId, requestId);
    }

    public Guid CompanyId { get; private set; }
    public string Name { get; private set; } = null!;

    public Company Company { get; private set; } = null!;
}
