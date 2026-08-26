using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Contrato das mutações de atribuição de papéis: o wrap do
/// RoleAssignmentService no POST de atribuição e a revogação com replay,
/// auditoria e bloqueio de último administrador no POST de revogação.
/// </summary>
public sealed class RoleUnassignmentEndpointTests
{
    [Fact]
    public async Task Assign_endpoint_contract_is_preserved()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();

        using var created = await client.PostAsJsonAsync(
            AssignRoute(scenario.CompanyId),
            AssignmentBody(requestId, scenario.TargetMembershipId, scenario.ReviewerRoleId));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var createdAssignmentId = await ReadRoleAssignmentIdAsync(created);

        using var replay = await client.PostAsJsonAsync(
            AssignRoute(scenario.CompanyId),
            AssignmentBody(requestId, scenario.TargetMembershipId, scenario.ReviewerRoleId));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(createdAssignmentId, await ReadRoleAssignmentIdAsync(replay));

        using var conflict = await client.PostAsJsonAsync(
            AssignRoute(scenario.CompanyId),
            AssignmentBody(requestId, scenario.TargetMembershipId, scenario.OperatorRoleId));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "{\"code\":\"idempotency_conflict\"}",
            await conflict.Content.ReadAsStringAsync());

        using var unknownRole = await client.PostAsJsonAsync(
            AssignRoute(scenario.CompanyId),
            AssignmentBody(Guid.NewGuid(), scenario.TargetMembershipId, Guid.NewGuid()));
        using var unknownMembership = await client.PostAsJsonAsync(
            AssignRoute(scenario.CompanyId),
            AssignmentBody(Guid.NewGuid(), Guid.NewGuid(), scenario.OperatorRoleId));
        foreach (var response in new[] { unknownRole, unknownMembership })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(
                "{\"code\":\"resource_not_found\"}",
                await response.Content.ReadAsStringAsync());
        }

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            1,
            await administrator.RoleAssignments.CountAsync(assignment =>
                assignment.RoleId == scenario.OperatorRoleId
                && assignment.MembershipId == scenario.TargetMembershipId));
        Assert.Equal(
            1,
            await administrator.AuditEvents.CountAsync(auditEvent =>
                auditEvent.Action == "foundation.roles.assign"
                && auditEvent.Outcome == AuditOutcome.Success));
        Assert.Equal(
            2,
            await administrator.AuditEvents.CountAsync(auditEvent =>
                auditEvent.Outcome == AuditOutcome.Denied
                && auditEvent.Action == "resource_not_found"));
    }

    [Fact]
    public async Task Unassign_soft_deletes_replays_and_audits()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var requestId = Guid.NewGuid();
        using var client = factory.CreateClient();

        using var removed = await client.PostAsJsonAsync(
            UnassignRoute(scenario.CompanyId),
            AssignmentBody(requestId, scenario.TargetMembershipId, scenario.OperatorRoleId));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var removedBody = JsonDocument.Parse(await removed.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(scenario.TargetAssignmentId, removedBody.GetProperty("roleAssignmentId").GetGuid());
        Assert.True(removedBody.GetProperty("removed").GetBoolean());

        using var replay = await client.PostAsJsonAsync(
            UnassignRoute(scenario.CompanyId),
            AssignmentBody(requestId, scenario.TargetMembershipId, scenario.OperatorRoleId));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(
            scenario.TargetAssignmentId,
            JsonDocument.Parse(await replay.Content.ReadAsStringAsync())
                .RootElement.GetProperty("roleAssignmentId").GetGuid());

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.RoleAssignments.Where(assignment =>
            assignment.Id == scenario.TargetAssignmentId).ToListAsync());
        var softDeleted = await administrator.RoleAssignments.IgnoreQueryFilters().SingleAsync(assignment =>
            assignment.Id == scenario.TargetAssignmentId);
        Assert.NotNull(softDeleted.DeletedAt);
        Assert.Equal("role_unassigned", softDeleted.DeletedReason);
        var success = await administrator.AuditEvents.SingleAsync(auditEvent =>
            auditEvent.Action == "foundation.roles.unassign" && auditEvent.Outcome == AuditOutcome.Success);
        Assert.Equal("role_assignment", success.ResourceType);
        Assert.Equal(scenario.TargetAssignmentId, success.ResourceId);
        Assert.Equal(
            1,
            await administrator.IdempotencyRecords.CountAsync(record =>
                record.CommandName == "foundation.roles.unassign"));
    }

    [Fact]
    public async Task Unassign_rejects_removing_the_last_admin()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            UnassignRoute(scenario.CompanyId),
            AssignmentBody(Guid.NewGuid(), scenario.ActorMembershipId, scenario.AdministratorRoleId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"last_admin_guard\"}",
            await response.Content.ReadAsStringAsync());
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var administratorAssignment = await administrator.RoleAssignments
            .IgnoreQueryFilters()
            .SingleAsync(assignment => assignment.MembershipId == scenario.ActorMembershipId);
        Assert.Equal(scenario.AdministratorRoleId, administratorAssignment.RoleId);
        Assert.Null(administratorAssignment.DeletedAt);
        var denial = await administrator.AuditEvents.SingleAsync(auditEvent =>
            auditEvent.Outcome == AuditOutcome.Denied && auditEvent.Action == "last_admin_guard");
        Assert.Equal(scenario.CompanyId, denial.RequestedCompanyId);
        Assert.Equal(factory.ActorUserId, denial.ActorUserId);
    }

    private static string AssignRoute(Guid companyId) =>
        $"/foundation/companies/{companyId}/role-assignments";

    private static string UnassignRoute(Guid companyId) =>
        $"/foundation/companies/{companyId}/role-unassignments";

    private static object AssignmentBody(Guid requestId, Guid membershipId, Guid roleId) =>
        new { requestId, membershipId, roleId };

    private static async Task<Guid> ReadRoleAssignmentIdAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("roleAssignmentId").GetGuid();

    private static async Task<UnassignmentScenario> SeedScenarioAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Obras", factory.ActorUserId, requestId);
        // O usuário do ator já é semeado pela factory autenticada.
        var target = new User(Guid.NewGuid(), "Bea", "bea@example.test");
        var actorMembership = new Membership(Guid.NewGuid(), company.Id, factory.ActorUserId, factory.ActorUserId, requestId);
        var targetMembership = new Membership(Guid.NewGuid(), company.Id, target.Id, factory.ActorUserId, requestId);
        var administratorRole = new Role(Guid.NewGuid(), company.Id, "Foundation administrator", factory.ActorUserId, requestId);
        var operatorRole = new Role(Guid.NewGuid(), company.Id, "Operator", factory.ActorUserId, requestId);
        var reviewerRole = new Role(Guid.NewGuid(), company.Id, "Reviewer", factory.ActorUserId, requestId);
        var capability = await administrator.Capabilities.SingleAsync(candidate =>
            candidate.Name == KnownCapabilities.RolesAssign);
        var grant = new RoleCapability(
            Guid.NewGuid(), company.Id, administratorRole.Id, capability.Id, CapabilityScope.Company, factory.ActorUserId, requestId);
        var administratorAssignment = new RoleAssignment(
            Guid.NewGuid(), company.Id, administratorRole.Id, actorMembership.Id, factory.ActorUserId, requestId);
        var targetAssignment = new RoleAssignment(
            Guid.NewGuid(), company.Id, operatorRole.Id, targetMembership.Id, factory.ActorUserId, requestId);

        administrator.AddRange(
            group,
            company,
            target,
            actorMembership,
            targetMembership,
            administratorRole,
            operatorRole,
            reviewerRole,
            grant,
            administratorAssignment,
            targetAssignment);
        await administrator.SaveChangesAsync();
        return new UnassignmentScenario(
            company.Id,
            actorMembership.Id,
            targetMembership.Id,
            administratorRole.Id,
            operatorRole.Id,
            reviewerRole.Id,
            targetAssignment.Id);
    }

    private sealed record UnassignmentScenario(
        Guid CompanyId,
        Guid ActorMembershipId,
        Guid TargetMembershipId,
        Guid AdministratorRoleId,
        Guid OperatorRoleId,
        Guid ReviewerRoleId,
        Guid TargetAssignmentId);
}
