using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class RoleAssignmentServiceIntegrationTests
{
    [Fact]
    public async Task Replaying_the_same_assignment_request_returns_the_stored_result_without_duplicate_state_or_audit()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);
        var requestId = Guid.NewGuid();

        var first = await InvokeAssignmentAsync(factory, scenario.CompanyId, scenario.AssignableRoleId, scenario.TargetMembershipId, requestId);
        var replay = await InvokeAssignmentAsync(factory, scenario.CompanyId, scenario.AssignableRoleId, scenario.TargetMembershipId, requestId);

        Assert.Null(ErrorCode(first));
        Assert.Null(ErrorCode(replay));
        Assert.Equal(first.Value?.RoleAssignmentId, replay.Value?.RoleAssignmentId);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator.RoleAssignments.CountAsync(assignment =>
            assignment.RoleId == scenario.AssignableRoleId && assignment.MembershipId == scenario.TargetMembershipId));
        Assert.Equal(1, await administrator.AuditEvents.CountAsync(auditEvent =>
            auditEvent.Action == "foundation.roles.assign" && auditEvent.Outcome == AuditOutcome.Success));
    }

    [Fact]
    public async Task Reusing_an_assignment_request_with_a_different_payload_returns_idempotency_conflict()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);
        var requestId = Guid.NewGuid();

        var first = await InvokeAssignmentAsync(factory, scenario.CompanyId, scenario.AssignableRoleId, scenario.TargetMembershipId, requestId);
        var conflict = await InvokeAssignmentAsync(factory, scenario.CompanyId, scenario.AlternateRoleId, scenario.TargetMembershipId, requestId);

        Assert.Null(ErrorCode(first));
        Assert.Equal("idempotency_conflict", ErrorCode(conflict));
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator.RoleAssignments.CountAsync(assignment => assignment.MembershipId == scenario.TargetMembershipId));
    }

    [Fact]
    public async Task Replaying_an_assignment_request_after_the_actor_loses_the_required_capability_is_denied_and_audited()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);
        var requestId = Guid.NewGuid();
        var first = await InvokeAssignmentAsync(factory, scenario.CompanyId, scenario.AssignableRoleId, scenario.TargetMembershipId, requestId);
        Assert.Null(ErrorCode(first));

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            var actorAssignment = await administrator.RoleAssignments.SingleAsync(assignment =>
                assignment.MembershipId != scenario.TargetMembershipId);
            administrator.RoleAssignments.Remove(actorAssignment);
            await administrator.SaveChangesAsync();
        }

        var replay = await InvokeAssignmentAsync(factory, scenario.CompanyId, scenario.AssignableRoleId, scenario.TargetMembershipId, requestId);

        Assert.Equal("capability_denied", ErrorCode(replay));
        await using var auditReader = factory.Fixture.CreateAdministratorDbContext();
        Assert.Single(await auditReader.AuditEvents.Where(auditEvent => auditEvent.Outcome == AuditOutcome.Denied).ToListAsync());
    }

    [Fact]
    public async Task Concurrent_assignment_retries_return_the_same_stored_result_without_audit_or_assignment_duplicates()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);
        var requestId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => InvokeAssignmentAsync(
            factory,
            scenario.CompanyId,
            scenario.AssignableRoleId,
            scenario.TargetMembershipId,
            requestId)));

        Assert.All(results, result => Assert.Null(ErrorCode(result)));
        Assert.Single(results.Select(result => result.Value!.RoleAssignmentId).Distinct());
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator.RoleAssignments.CountAsync(assignment =>
            assignment.RoleId == scenario.AssignableRoleId && assignment.MembershipId == scenario.TargetMembershipId));
        Assert.Equal(1, await administrator.AuditEvents.CountAsync(auditEvent =>
            auditEvent.Action == "foundation.roles.assign" && auditEvent.Outcome == AuditOutcome.Success));
    }

    [Fact]
    public async Task Cross_company_target_membership_is_hidden_as_not_found_and_the_denial_is_audited()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory, crossCompanyTarget: true);

        var result = await InvokeAssignmentAsync(
            factory,
            scenario.CompanyId,
            scenario.AssignableRoleId,
            scenario.TargetMembershipId,
            Guid.NewGuid());

        Assert.Equal("resource_not_found", ErrorCode(result));
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var denial = await administrator.AuditEvents.SingleAsync(auditEvent => auditEvent.Outcome == AuditOutcome.Denied);
        Assert.Equal(factory.ActorUserId, denial.ActorUserId);
        Assert.Equal(scenario.CompanyId, denial.RequestedCompanyId);
        Assert.Equal(factory.CorrelationId, denial.CorrelationId);
        Assert.Equal("resource_not_found", denial.Reason);
    }

    internal static async Task<AssignmentScenario> SeedScenarioAsync(
        FoundationApiFactory factory,
        bool crossCompanyTarget = false)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Obras", factory.ActorUserId, requestId);
        var otherCompany = new Company(Guid.NewGuid(), group.Id, "Vinx Serviços", factory.ActorUserId, requestId);
        var actor = new User(factory.ActorUserId, "Ana", "ana@example.test");
        var target = new User(Guid.NewGuid(), "Bea", "bea@example.test");
        var actorMembership = new Membership(Guid.NewGuid(), company.Id, actor.Id, factory.ActorUserId, requestId);
        var targetMembership = new Membership(
            Guid.NewGuid(),
            crossCompanyTarget ? otherCompany.Id : company.Id,
            target.Id,
            factory.ActorUserId,
            requestId);
        var administratorRole = new Role(Guid.NewGuid(), company.Id, "Foundation administrator", factory.ActorUserId, requestId);
        var assignableRole = new Role(Guid.NewGuid(), company.Id, "Operator", factory.ActorUserId, requestId);
        var alternateRole = new Role(Guid.NewGuid(), company.Id, "Reviewer", factory.ActorUserId, requestId);
        var capability = new Capability(Guid.NewGuid(), "foundation.roles.assign");
        var grant = new RoleCapability(
            Guid.NewGuid(), company.Id, administratorRole.Id, capability.Id, CapabilityScope.Company, factory.ActorUserId, requestId);
        var administratorAssignment = new RoleAssignment(
            Guid.NewGuid(), company.Id, administratorRole.Id, actorMembership.Id, factory.ActorUserId, requestId);

        administrator.AddRange(
            group,
            company,
            otherCompany,
            actor,
            target,
            actorMembership,
            targetMembership,
            administratorRole,
            assignableRole,
            alternateRole,
            capability,
            grant,
            administratorAssignment);
        await administrator.SaveChangesAsync();
        return new AssignmentScenario(company.Id, assignableRole.Id, alternateRole.Id, targetMembership.Id);
    }

    internal static async Task<AuthorizationResult<AssignRoleToMembershipResult>> InvokeAssignmentAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid roleId,
        Guid membershipId,
        Guid requestId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<RoleAssignmentService>();
        return await service.AssignRoleToMembershipAsync(
            new ActorContext(factory.ActorUserId, factory.CorrelationId),
            new AssignRoleToMembershipCommand(companyId, roleId, membershipId, requestId),
            CancellationToken.None);
    }

    internal static string? ErrorCode(AuthorizationResult<AssignRoleToMembershipResult> result)
    {
        return result.ErrorCode?.ToStableCode();
    }

    internal sealed record AssignmentScenario(
        Guid CompanyId,
        Guid AssignableRoleId,
        Guid AlternateRoleId,
        Guid TargetMembershipId);
}
