using Vinx.Erp.Foundation.Domain;
using Xunit;

namespace Vinx.Erp.ArchitectureTests;

public sealed class FoundationEntityContractTests
{
    private static readonly Type[] MutableEntityTypes =
    [
        typeof(Group),
        typeof(Company),
        typeof(Unit),
        typeof(Team),
        typeof(Membership),
        typeof(Employee),
        typeof(Role),
        typeof(RoleCapability),
        typeof(RoleAssignment),
        typeof(FileRecord)
    ];

    private static readonly Type[] CompanyScopedEntityTypes =
    [
        typeof(Unit),
        typeof(Team),
        typeof(Membership),
        typeof(Employee),
        typeof(Role),
        typeof(RoleCapability),
        typeof(RoleAssignment),
        typeof(FileRecord),
        typeof(IdempotencyRecord)
    ];

    [Fact]
    public void Mutable_entities_share_the_common_lifecycle_base()
    {
        var lifecycleBase = typeof(SoftDeletableEntity);

        Assert.All(
            MutableEntityTypes,
            entityType => Assert.True(
                lifecycleBase.IsAssignableFrom(entityType),
                $"{entityType.Name} must inherit {lifecycleBase.Name}."));
    }

    [Fact]
    public void Mutable_entities_expose_soft_delete_and_concurrency_contracts()
    {
        var softDeletable = typeof(ISoftDeletable);
        var concurrencyVersioned = typeof(IConcurrencyVersioned);

        Assert.All(MutableEntityTypes, entityType => Assert.True(softDeletable.IsAssignableFrom(entityType)));
        Assert.All(MutableEntityTypes, entityType => Assert.True(concurrencyVersioned.IsAssignableFrom(entityType)));
    }

    [Fact]
    public void Only_company_owned_entities_expose_company_scope()
    {
        var companyScoped = typeof(ICompanyScoped);

        Assert.All(CompanyScopedEntityTypes, entityType => Assert.True(companyScoped.IsAssignableFrom(entityType)));
        Assert.False(companyScoped.IsAssignableFrom(typeof(Group)));
        Assert.False(companyScoped.IsAssignableFrom(typeof(Company)));
        Assert.False(companyScoped.IsAssignableFrom(typeof(User)));
    }
}
