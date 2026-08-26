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
/// Contrato da cópia de permissões entre membresias: substituição do conjunto
/// de papéis do alvo, no-op de conjuntos idênticos, rejeição de fonte igual ao
/// destino, bloqueio de estado final sem administrador e conflito de replay.
/// </summary>
public sealed class RolePermissionsCopyEndpointTests
{
    [Fact]
    public async Task Copy_replaces_target_roles_and_audits()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var replaced = await client.PostAsJsonAsync(
            CopyRoute(scenario.CompanyId),
            CopyBody(Guid.NewGuid(), scenario.SourceMembershipId, scenario.TargetMembershipId));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var replacedBody = JsonDocument.Parse(await replaced.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(scenario.TargetMembershipId, replacedBody.GetProperty("targetMembershipId").GetGuid());
        Assert.Equal(
            new[] { scenario.OperatorRoleId, scenario.ReviewerRoleId }.Order(),
            replacedBody.GetProperty("appliedRoleIds").EnumerateArray().Select(id => id.GetGuid()).Order());

        using var noOp = await client.PostAsJsonAsync(
            CopyRoute(scenario.CompanyId),
            CopyBody(Guid.NewGuid(), scenario.SourceMembershipId, scenario.TargetMembershipId));
        Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);

        using var selfCopy = await client.PostAsJsonAsync(
            CopyRoute(scenario.CompanyId),
            CopyBody(Guid.NewGuid(), scenario.SourceMembershipId, scenario.SourceMembershipId));
        Assert.Equal(HttpStatusCode.BadRequest, selfCopy.StatusCode);
        Assert.Equal(
            "{\"code\":\"invalid_request\"}",
            await selfCopy.Content.ReadAsStringAsync());

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var targetRoleIds = await administrator.RoleAssignments
            .Where(assignment => assignment.MembershipId == scenario.TargetMembershipId)
            .Select(assignment => assignment.RoleId)
            .ToListAsync();
        Assert.Equal(
            new[] { scenario.OperatorRoleId, scenario.ReviewerRoleId }.Order(),
            targetRoleIds.Order());
        Assert.Single(await administrator.RoleAssignments.IgnoreQueryFilters().Where(assignment =>
            assignment.MembershipId == scenario.TargetMembershipId
            && assignment.DeletedAt != null).ToListAsync());
        Assert.Equal(
            2,
            await administrator.AuditEvents.CountAsync(auditEvent =>
                auditEvent.Action == "foundation.roles.copy"
                && auditEvent.Outcome == AuditOutcome.Success));
        Assert.Equal(
            2,
            await administrator.IdempotencyRecords.CountAsync(record =>
                record.CommandName == "foundation.roles.copy"));
    }

    [Fact]
    public async Task Copy_rejects_zero_admin_post_state()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            CopyRoute(scenario.CompanyId),
            CopyBody(Guid.NewGuid(), scenario.SourceMembershipId, scenario.ActorMembershipId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"last_admin_guard\"}",
            await response.Content.ReadAsStringAsync());
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var actorRoleIds = await administrator.RoleAssignments
            .Where(assignment => assignment.MembershipId == scenario.ActorMembershipId)
            .Select(assignment => assignment.RoleId)
            .ToListAsync();
        Assert.Equal(new[] { scenario.AdministratorRoleId }, actorRoleIds);
        Assert.Single(await administrator.AuditEvents.Where(auditEvent =>
            auditEvent.Outcome == AuditOutcome.Denied && auditEvent.Action == "last_admin_guard").ToListAsync());
    }

    [Fact]
    public async Task Copy_replay_with_divergent_payload_conflicts()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var requestId = Guid.NewGuid();
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync(
            CopyRoute(scenario.CompanyId),
            CopyBody(requestId, scenario.SourceMembershipId, scenario.TargetMembershipId));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var conflict = await client.PostAsJsonAsync(
            CopyRoute(scenario.CompanyId),
            CopyBody(requestId, scenario.SourceMembershipId, scenario.ActorMembershipId));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "{\"code\":\"idempotency_conflict\"}",
            await conflict.Content.ReadAsStringAsync());

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            scenario.AdministratorRoleId,
            await administrator.RoleAssignments
                .Where(assignment => assignment.MembershipId == scenario.ActorMembershipId)
                .Select(assignment => assignment.RoleId)
                .SingleAsync());
        Assert.Single(await administrator.AuditEvents.Where(auditEvent =>
            auditEvent.Action == "idempotency_conflict" && auditEvent.Outcome == AuditOutcome.Denied).ToListAsync());
    }

    private static string CopyRoute(Guid companyId) =>
        $"/foundation/companies/{companyId}/permission-copies";

    private static object CopyBody(Guid requestId, Guid sourceMembershipId, Guid targetMembershipId) =>
        new { requestId, sourceMembershipId, targetMembershipId };

    private static async Task<CopyScenario> SeedScenarioAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Obras", factory.ActorUserId, requestId);
        // O usuário do ator já é semeado pela factory autenticada.
        var source = new User(Guid.NewGuid(), "Bea", "bea@example.test");
        var target = new User(Guid.NewGuid(), "Caio", "caio@example.test");
        var actorMembership = new Membership(Guid.NewGuid(), company.Id, factory.ActorUserId, factory.ActorUserId, requestId);
        var sourceMembership = new Membership(Guid.NewGuid(), company.Id, source.Id, factory.ActorUserId, requestId);
        var targetMembership = new Membership(Guid.NewGuid(), company.Id, target.Id, factory.ActorUserId, requestId);
        var administratorRole = new Role(Guid.NewGuid(), company.Id, "Foundation administrator", factory.ActorUserId, requestId);
        var operatorRole = new Role(Guid.NewGuid(), company.Id, "Operator", factory.ActorUserId, requestId);
        var reviewerRole = new Role(Guid.NewGuid(), company.Id, "Reviewer", factory.ActorUserId, requestId);
        var auditorRole = new Role(Guid.NewGuid(), company.Id, "Auditor", factory.ActorUserId, requestId);
        var capability = await administrator.Capabilities.SingleAsync(candidate =>
            candidate.Name == KnownCapabilities.RolesAssign);
        var grant = new RoleCapability(
            Guid.NewGuid(), company.Id, administratorRole.Id, capability.Id, CapabilityScope.Company, factory.ActorUserId, requestId);
        var administratorAssignment = new RoleAssignment(
            Guid.NewGuid(), company.Id, administratorRole.Id, actorMembership.Id, factory.ActorUserId, requestId);
        var operatorAssignment = new RoleAssignment(
            Guid.NewGuid(), company.Id, operatorRole.Id, sourceMembership.Id, factory.ActorUserId, requestId);
        var reviewerAssignment = new RoleAssignment(
            Guid.NewGuid(), company.Id, reviewerRole.Id, sourceMembership.Id, factory.ActorUserId, requestId);
        var auditorAssignment = new RoleAssignment(
            Guid.NewGuid(), company.Id, auditorRole.Id, targetMembership.Id, factory.ActorUserId, requestId);

        administrator.AddRange(
            group,
            company,
            source,
            target,
            actorMembership,
            sourceMembership,
            targetMembership,
            administratorRole,
            operatorRole,
            reviewerRole,
            auditorRole,
            grant,
            administratorAssignment,
            operatorAssignment,
            reviewerAssignment,
            auditorAssignment);
        await administrator.SaveChangesAsync();
        return new CopyScenario(
            company.Id,
            actorMembership.Id,
            sourceMembership.Id,
            targetMembership.Id,
            administratorRole.Id,
            operatorRole.Id,
            reviewerRole.Id);
    }

    private sealed record CopyScenario(
        Guid CompanyId,
        Guid ActorMembershipId,
        Guid SourceMembershipId,
        Guid TargetMembershipId,
        Guid AdministratorRoleId,
        Guid OperatorRoleId,
        Guid ReviewerRoleId);
}
