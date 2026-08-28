using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class CrossCompanyConstraintTests
{
    [Fact]
    public async Task Role_assignment_with_a_membership_from_another_company_is_rejected_by_the_database()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await using var administrator = fixture.CreateAdministratorDbContext();

        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", actorId, requestId);
        var user = new User(Guid.NewGuid(), "Ana", "ana@example.test");
        var companyA = new Company(Guid.NewGuid(), group.Id, "Vinx Obras", actorId, requestId);
        var companyB = new Company(Guid.NewGuid(), group.Id, "Vinx Serviços", actorId, requestId);
        var roleA = new Role(Guid.NewGuid(), companyA.Id, "Operator", actorId, requestId);
        var membershipB = new Membership(Guid.NewGuid(), companyB.Id, user.Id, actorId, requestId);

        administrator.AddRange(group, user, companyA, companyB, roleA, membershipB);
        await administrator.SaveChangesAsync();

        var invalid = new RoleAssignment(
            Guid.NewGuid(), companyA.Id, roleA.Id, membershipB.Id, actorId, requestId);
        administrator.RoleAssignments.Add(invalid);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await administrator.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, (exception.InnerException as PostgresException)?.SqlState);
    }
}
