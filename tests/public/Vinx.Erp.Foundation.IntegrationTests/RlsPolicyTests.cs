using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class RlsPolicyTests
{
    [Fact]
    public async Task Runtime_command_context_is_transaction_local_and_handles_optional_company()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var (companyA, companyB) = await SeedTwoCompaniesAsync(fixture);
        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using (var transaction = await runtime.Database.BeginTransactionAsync())
        {
            await FoundationRlsContext.ApplyCommandAsync(
                runtime.Database,
                actor,
                companyA.Id,
                CancellationToken.None);

            var visibleCompanies = await runtime.Companies.AsNoTracking().ToListAsync();
            var company = Assert.Single(visibleCompanies);
            Assert.Equal(companyA.Id, company.Id);
            Assert.DoesNotContain(visibleCompanies, company => company.Id == companyB.Id);

            var userSetting = await runtime.Database.SqlQuery<string>(
                $"SELECT current_setting('app.user_id', true) AS \"Value\"").SingleAsync();
            var companySetting = await runtime.Database.SqlQuery<string>(
                $"SELECT current_setting('app.company_id', true) AS \"Value\"").SingleAsync();
            var correlationSetting = await runtime.Database.SqlQuery<string>(
                $"SELECT current_setting('app.correlation_id', true) AS \"Value\"").SingleAsync();

            Assert.Equal(actor.UserId.ToString(), userSetting);
            Assert.Equal(companyA.Id.ToString(), companySetting);
            Assert.Equal(actor.CorrelationId.ToString(), correlationSetting);
        }

        await using (var resetTransaction = await runtime.Database.BeginTransactionAsync())
        {
            Assert.Null(await runtime.Database.SqlQuery<string?>(
                $"SELECT current_setting('app.user_id', true) AS \"Value\"").SingleAsync());
            Assert.Null(await runtime.Database.SqlQuery<string?>(
                $"SELECT current_setting('app.company_id', true) AS \"Value\"").SingleAsync());
            Assert.Null(await runtime.Database.SqlQuery<string?>(
                $"SELECT current_setting('app.correlation_id', true) AS \"Value\"").SingleAsync());
        }

        await using var nullCompanyTransaction = await runtime.Database.BeginTransactionAsync();
        await FoundationRlsContext.ApplyCommandAsync(
            runtime.Database,
            actor,
            null,
            CancellationToken.None);

        Assert.Empty(await runtime.Companies.AsNoTracking().ToListAsync());
        Assert.Equal(
            string.Empty,
            await runtime.Database.SqlQuery<string>(
                $"SELECT current_setting('app.company_id', true) AS \"Value\"").SingleAsync());
    }

    [Fact]
    public async Task Runtime_query_without_company_context_returns_zero_membership_rows()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var (companyA, _) = await SeedTwoCompaniesAsync(fixture);

        await using var administrator = fixture.CreateAdministratorDbContext();
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var user = new User(actorId, "Membership actor", "membership-actor@example.test");
        var membership = new Membership(Guid.NewGuid(), companyA.Id, user.Id, actorId, requestId);
        administrator.AddRange(user, membership);
        await administrator.SaveChangesAsync();

        await using var runtime = fixture.CreateRuntimeDbContext();

        Assert.Empty(await runtime.Memberships.AsNoTracking().ToListAsync());
    }

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

        var company = Assert.Single(visibleCompanies);
        Assert.Equal(companyA.Id, company.Id);
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

    [Fact]
    public async Task Runtime_authorized_company_context_reads_only_the_server_resolved_set()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var (companyA, companyB) = await SeedTwoCompaniesAsync(fixture);

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.authorized_company_ids', {companyA.Id.ToString()}, true)");

        var visibleCompanies = await runtime.Companies.AsNoTracking().ToListAsync();

        Assert.Equal([companyA.Id], visibleCompanies.Select(company => company.Id));
        Assert.DoesNotContain(visibleCompanies, company => company.Id == companyB.Id);
    }

    [Fact]
    public async Task Authorized_company_context_does_not_authorize_writes_without_exact_company_context()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var (companyA, _) = await SeedTwoCompaniesAsync(fixture);
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await using (var administrator = fixture.CreateAdministratorDbContext())
        {
            var user = new User(actorId, "Read actor", "read-actor@example.test");
            administrator.Add(user);
            administrator.Add(new Membership(Guid.NewGuid(), companyA.Id, actorId, actorId, requestId));
            await administrator.SaveChangesAsync();
        }

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.authorized_company_ids', {companyA.Id.ToString()}, true)");
        var visibleCompanies = await runtime.Companies.AsNoTracking().ToListAsync();
        Assert.Single(visibleCompanies);

        var affected = await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE foundation.memberships SET deleted_at = clock_timestamp() WHERE company_id = {companyA.Id}");

        Assert.Equal(0, affected);
    }

    [Fact]
    public async Task Concurrent_authorized_company_contexts_keep_Maria_and_Bruno_isolated()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var (companyA, companyB) = await SeedTwoCompaniesAsync(fixture);

        var maria = ReadAuthorizedCompaniesAsync(fixture, companyA.Id);
        var bruno = ReadAuthorizedCompaniesAsync(fixture, companyB.Id);
        var results = await Task.WhenAll(maria, bruno);

        Assert.Equal([companyA.Id], results[0]);
        Assert.Equal([companyB.Id], results[1]);
    }

    private static async Task<IReadOnlyList<Guid>> ReadAuthorizedCompaniesAsync(
        FoundationPostgresFixture fixture,
        Guid companyId)
    {
        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.authorized_company_ids', {companyId.ToString()}, true)");
        return await runtime.Companies.AsNoTracking().Select(company => company.Id).ToListAsync();
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
        var fileA = NewFileRecord(companyA.Id, membershipA.Id, "a/test.txt", actorId, requestId);
        var fileB = NewFileRecord(companyB.Id, membershipB.Id, "b/test.txt", actorId, requestId);

        administrator.AddRange(userA, userB, membershipA, membershipB, fileA, fileB);
        await administrator.SaveChangesAsync();
        return (companyA, companyB, fileB);
    }

    private static FileRecord NewFileRecord(
        Guid companyId,
        Guid membershipId,
        string fileName,
        Guid actorId,
        Guid requestId) => new(
        Guid.NewGuid(),
        companyId,
        membershipId,
        fileName,
        ".txt",
        1L,
        "text/plain",
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "internal",
        "test",
        DateTimeOffset.UtcNow.AddDays(30),
        actorId,
        requestId);
}
