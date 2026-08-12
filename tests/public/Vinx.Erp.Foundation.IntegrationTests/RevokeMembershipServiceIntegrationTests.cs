using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class RevokeMembershipServiceIntegrationTests
{
    [Fact]
    public async Task Revoking_an_active_same_company_membership_soft_deletes_it_and_writes_one_audit_and_idempotency_record()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);

        var result = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Equal(scenario.TargetMembershipId, result.Value!.MembershipId);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.NotNull(await DeletedAtAsync(administrator, scenario.TargetMembershipId));
        Assert.Equal(1, await administrator.IdempotencyRecords.CountAsync(record =>
            record.CommandName == "foundation.memberships.revoke"));
        Assert.Equal(1, await administrator.AuditEvents.CountAsync(audit =>
            audit.Action == "foundation.memberships.revoke" && audit.Outcome == AuditOutcome.Success));
    }

    [Fact]
    public async Task Matching_replay_returns_original_result_and_different_payload_conflicts()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);
        var requestId = Guid.NewGuid();

        var first = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, requestId);
        var replay = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, requestId);
        var conflict = await RevokeAsync(factory, scenario.CompanyId, scenario.AlternateMembershipId, requestId);

        Assert.True(first.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal("idempotency_conflict", ErrorCode(conflict));
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Null(await DeletedAtAsync(administrator, scenario.AlternateMembershipId));
    }

    [Fact]
    public async Task Authorization_is_evaluated_before_a_matching_replay()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);
        var requestId = Guid.NewGuid();
        Assert.True((await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, requestId)).IsSuccess);

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            administrator.RoleAssignments.Remove(await administrator.RoleAssignments.SingleAsync(assignment =>
                assignment.MembershipId == scenario.ActorMembershipId));
            await administrator.SaveChangesAsync();
        }

        var replay = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, requestId);

        Assert.Equal("capability_denied", ErrorCode(replay));
    }

    [Fact]
    public async Task Cross_company_target_is_hidden_as_not_found()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory, crossCompanyTarget: true);

        var result = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, Guid.NewGuid());

        Assert.Equal("resource_not_found", ErrorCode(result));
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Null(await DeletedAtAsync(administrator, scenario.TargetMembershipId));
    }

    [Fact]
    public async Task Non_company_scope_is_denied()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory, administratorScope: CapabilityScope.Self);

        var result = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, Guid.NewGuid());

        Assert.Equal("capability_denied", ErrorCode(result));
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Null(await DeletedAtAsync(administrator, scenario.TargetMembershipId));
    }

    private static async Task<RevocationScenario> SeedScenarioAsync(
        FoundationApiFactory factory,
        bool crossCompanyTarget = false,
        CapabilityScope administratorScope = CapabilityScope.Company)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Alfa", factory.ActorUserId, requestId);
        var otherCompany = new Company(Guid.NewGuid(), group.Id, "Beta", factory.ActorUserId, requestId);
        var actor = new User(factory.ActorUserId, "Ana", "ana@example.test");
        var target = new User(Guid.NewGuid(), "Bea", "bea@example.test");
        var alternate = new User(Guid.NewGuid(), "Caio", "caio@example.test");
        var actorMembership = new Membership(Guid.NewGuid(), company.Id, actor.Id, factory.ActorUserId, requestId);
        var targetMembership = new Membership(
            Guid.NewGuid(), crossCompanyTarget ? otherCompany.Id : company.Id, target.Id, factory.ActorUserId, requestId);
        var alternateMembership = new Membership(
            Guid.NewGuid(), company.Id, alternate.Id, factory.ActorUserId, requestId);
        var role = new Role(Guid.NewGuid(), company.Id, "Foundation administrator", factory.ActorUserId, requestId);
        var capability = await administrator.Capabilities.SingleAsync(candidate =>
            candidate.Name == KnownCapabilities.MembershipsRevoke);

        administrator.AddRange(
            group, company, otherCompany, actor, target, alternate, actorMembership, targetMembership, alternateMembership, role,
            new RoleCapability(Guid.NewGuid(), company.Id, role.Id, capability.Id, administratorScope, factory.ActorUserId, requestId),
            new RoleAssignment(Guid.NewGuid(), company.Id, role.Id, actorMembership.Id, factory.ActorUserId, requestId));
        await administrator.SaveChangesAsync();
        return new RevocationScenario(company.Id, actorMembership.Id, targetMembership.Id, alternateMembership.Id);
    }

    private static async Task<AuthorizationResult<RevokeMembershipResult>> RevokeAsync(
        FoundationApiFactory factory, Guid companyId, Guid membershipId, Guid requestId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<RevokeMembershipService>();
        return await service.RevokeAsync(
            new ActorContext(factory.ActorUserId, factory.CorrelationId),
            new RevokeMembershipCommand(companyId, membershipId, requestId),
            CancellationToken.None);
    }

    private static Task<DateTimeOffset?> DeletedAtAsync(Persistence.FoundationDbContext administrator, Guid membershipId) =>
        administrator.Memberships.IgnoreQueryFilters().Where(membership => membership.Id == membershipId)
            .Select(membership => membership.DeletedAt).SingleAsync();

    private static string? ErrorCode(AuthorizationResult<RevokeMembershipResult> result) =>
        result.ErrorCode?.ToStableCode();

    private sealed record RevocationScenario(
        Guid CompanyId, Guid ActorMembershipId, Guid TargetMembershipId, Guid AlternateMembershipId);
}
