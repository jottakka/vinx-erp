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

    [Fact]
    public async Task Updating_a_user_and_session_advances_row_versions_and_uses_database_first_seen_time()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var user = new User(Guid.NewGuid(), "Sessão", "row-version-session@example.test");
        var session = new UserSession(
            Guid.NewGuid(),
            user.Id,
            "https://project.supabase.co/auth/v1",
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-08-09T12:00:00+00:00"),
            DateTimeOffset.Parse("2026-08-09T13:00:00+00:00"));

        await using var administrator = fixture.CreateAdministratorDbContext();
        var beforeSave = DateTimeOffset.UtcNow;
        administrator.AddRange(user, session);
        await administrator.SaveChangesAsync();
        var afterSave = DateTimeOffset.UtcNow;

        Assert.Equal(1L, user.RowVersion);
        Assert.Equal(1L, session.RowVersion);
        Assert.InRange(session.FirstSeenAt, beforeSave, afterSave);

        administrator.Entry(user).Property(entity => entity.SessionsValidAfter).CurrentValue = afterSave;
        session.Revoke(user.Id, afterSave, "logout");
        await administrator.SaveChangesAsync();

        Assert.Equal(2L, user.RowVersion);
        Assert.Equal(2L, session.RowVersion);
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
