using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class RowVersionTests
{
    [Fact]
    public async Task Updating_a_mutable_entity_advances_its_row_version()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var (groupId, companyId) = await SeedCompanyAsync(fixture);

        await using var administrator = fixture.CreateAdministratorDbContext();
        var company = await administrator.Companies.SingleAsync(entity => entity.Id == companyId);
        Assert.Equal(groupId, company.GroupId);
        Assert.Equal(1L, company.RowVersion);

        company.Rename("Vinx Obras Renovada", Guid.NewGuid(), Guid.NewGuid());
        await administrator.SaveChangesAsync();

        Assert.Equal(2L, company.RowVersion);
    }

    private static async Task<(Guid GroupId, Guid CompanyId)> SeedCompanyAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", actorId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Obras", actorId, requestId);

        administrator.AddRange(group, company);
        await administrator.SaveChangesAsync();
        return (group.Id, company.Id);
    }
}
