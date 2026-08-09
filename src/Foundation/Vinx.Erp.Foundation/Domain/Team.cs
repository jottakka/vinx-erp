namespace Vinx.Erp.Foundation.Domain;

public sealed class Team : SoftDeletableEntity, ICompanyScoped
{
    private Team()
    {
    }

    public Team(Guid id, Guid companyId, Guid unitId, string name, Guid actorUserId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        CompanyId = companyId;
        UnitId = unitId;
        Name = name;
        InitializeLifecycle(id, actorUserId, requestId);
    }

    public Guid CompanyId { get; private set; }
    public Guid UnitId { get; private set; }
    public string Name { get; private set; } = null!;

    public Company Company { get; private set; } = null!;
    public Unit Unit { get; private set; } = null!;
}
