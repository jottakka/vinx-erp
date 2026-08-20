using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class CapabilityEvaluatorIntegrationTests
{
    [Theory]
    [InlineData(CapabilityScope.Self, false)]
    [InlineData(CapabilityScope.Team, false)]
    [InlineData(CapabilityScope.Unit, false)]
    [InlineData(CapabilityScope.Company, true)]
    [InlineData(CapabilityScope.Group, false)]
    public async Task Company_command_requires_an_exact_company_grant(
        CapabilityScope grantedScope,
        bool expected)
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var scenario = await SeedScenarioAsync(fixture, grantedScope);

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        await SetCompanyContextAsync(runtime, scenario.CompanyId);
        var evaluator = new CapabilityEvaluator(runtime);

        var hasGrant = await evaluator.HasGrantAsync(
            scenario.CompanyId,
            scenario.MembershipId,
            new CapabilityRequirement("foundation.roles.assign", CapabilityScope.Company),
            CancellationToken.None);

        Assert.Equal(expected, hasGrant);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Missing_runtime_company_context_fails_closed()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var scenario = await SeedScenarioAsync(fixture, CapabilityScope.Company);

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        var evaluator = new CapabilityEvaluator(runtime);

        var hasGrant = await evaluator.HasGrantAsync(
            scenario.CompanyId,
            scenario.MembershipId,
            new CapabilityRequirement("foundation.roles.assign", CapabilityScope.Company),
            CancellationToken.None);

        Assert.False(hasGrant);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Another_company_assignment_and_grant_never_authorize_the_requested_company()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var first = await SeedScenarioAsync(fixture, CapabilityScope.Company);
        var second = await SeedScenarioAsync(fixture, CapabilityScope.Company);

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        await SetCompanyContextAsync(runtime, first.CompanyId);
        var evaluator = new CapabilityEvaluator(runtime);

        var hasGrant = await evaluator.HasGrantAsync(
            first.CompanyId,
            second.MembershipId,
            new CapabilityRequirement("foundation.roles.assign", CapabilityScope.Company),
            CancellationToken.None);

        Assert.False(hasGrant);
        await transaction.RollbackAsync();
    }

    private static async Task<EvaluatorScenario> SeedScenarioAsync(
        FoundationPostgresFixture fixture,
        CapabilityScope scope)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), $"Group {Guid.NewGuid():N}", actorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, $"Company {Guid.NewGuid():N}", actorUserId, requestId);
        var user = new User(actorUserId, $"User {Guid.NewGuid():N}", $"{Guid.NewGuid():N}@example.test");
        var membership = new Membership(Guid.NewGuid(), company.Id, user.Id, actorUserId, requestId);
        var role = new Role(Guid.NewGuid(), company.Id, $"Role {Guid.NewGuid():N}", actorUserId, requestId);
        var capability = await administrator.Capabilities.SingleAsync(candidate =>
            candidate.Name == KnownCapabilities.RolesAssign);
        var assignment = new RoleAssignment(Guid.NewGuid(), company.Id, role.Id, membership.Id, actorUserId, requestId);
        var grant = new RoleCapability(Guid.NewGuid(), company.Id, role.Id, capability.Id, scope, actorUserId, requestId);
        administrator.AddRange(group, company, user, membership, role, assignment, grant);
        await administrator.SaveChangesAsync();
        return new EvaluatorScenario(company.Id, membership.Id);
    }

    private static Task<int> SetCompanyContextAsync(FoundationDbContext database, Guid companyId) =>
        database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {companyId.ToString()}, true)");

    private sealed record EvaluatorScenario(Guid CompanyId, Guid MembershipId);
}
