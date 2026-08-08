using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class RlsPolicyTests
{
    [Fact]
    public async Task Runtime_query_without_company_context_returns_zero_company_rows()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var company = await SeedCompanyAsync(fixture);

        await using var runtime = fixture.CreateRuntimeDbContext();

        Assert.Empty(await runtime.Companies.AsNoTracking().ToListAsync());
        Assert.NotEqual(Guid.Empty, company.Id);
    }

    [Fact]
    public async Task Runtime_company_context_returns_only_the_requested_company()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var (companyA, companyB) = await SeedTwoCompaniesAsync(fixture);

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {companyA.Id.ToString()}, true)");

        var visibleCompanies = await runtime.Companies.AsNoTracking().ToListAsync();

        Assert.Collection(visibleCompanies, company => Assert.Equal(companyA.Id, company.Id));
        Assert.DoesNotContain(visibleCompanies, company => company.Id == companyB.Id);
    }

    [Fact]
    public async Task Runtime_company_context_can_read_its_own_group()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var (companyA, _) = await SeedTwoCompaniesAsync(fixture);

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {companyA.Id.ToString()}, true)");

        var group = await runtime.Groups.AsNoTracking().SingleAsync();

        Assert.Equal(companyA.GroupId, group.Id);
    }

    [Fact]
    public async Task Runtime_company_context_cannot_read_file_metadata_for_another_company()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var (companyA, _, fileB) = await SeedCompaniesAndFilesAsync(fixture);

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {companyA.Id.ToString()}, true)");

        Assert.Null(await runtime.Files.AsNoTracking()
            .SingleOrDefaultAsync(file => file.Id == fileB.Id));
    }

    private static async Task<Company> SeedCompanyAsync(FoundationPostgresFixture fixture)
    {
        var (company, _) = await SeedTwoCompaniesAsync(fixture);
        return company;
    }

    private static async Task<(Company CompanyA, Company CompanyB)> SeedTwoCompaniesAsync(
        FoundationPostgresFixture fixture)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", actorId, requestId);
        var companyA = new Company(Guid.NewGuid(), group.Id, "Vinx Obras", actorId, requestId);
        var companyB = new Company(Guid.NewGuid(), group.Id, "Vinx Serviços", actorId, requestId);

        administrator.AddRange(group, companyA, companyB);
        await administrator.SaveChangesAsync();
        return (companyA, companyB);
    }

    private static async Task<(Company CompanyA, Company CompanyB, FileRecord FileB)> SeedCompaniesAndFilesAsync(
        FoundationPostgresFixture fixture)
    {
        var (companyA, companyB) = await SeedTwoCompaniesAsync(fixture);
        await using var administrator = fixture.CreateAdministratorDbContext();
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var userA = new User(Guid.NewGuid(), "Ana", "ana@example.test");
        var userB = new User(Guid.NewGuid(), "Bea", "bea@example.test");
        var membershipA = new Membership(Guid.NewGuid(), companyA.Id, userA.Id, actorId, requestId);
        var membershipB = new Membership(Guid.NewGuid(), companyB.Id, userB.Id, actorId, requestId);
        var fileA = new FileRecord(
            Guid.NewGuid(), companyA.Id, membershipA.Id, "internal", "checksum-a", "a/test.txt", true, actorId, requestId);
        var fileB = new FileRecord(
            Guid.NewGuid(), companyB.Id, membershipB.Id, "internal", "checksum-b", "b/test.txt", true, actorId, requestId);

        administrator.AddRange(userA, userB, membershipA, membershipB, fileA, fileB);
        await administrator.SaveChangesAsync();
        return (companyA, companyB, fileB);
    }
}
