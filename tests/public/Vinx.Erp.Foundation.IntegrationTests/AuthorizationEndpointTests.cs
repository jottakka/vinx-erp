using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AuthorizationEndpointTests
{
    [Fact]
    public async Task Capability_map_without_a_company_returns_the_stable_context_error_and_audits_the_denial()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/foundation/capabilities");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("company_context_required", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var auditEvent = await administrator.AuditEvents.SingleAsync();
        Assert.Equal(factory.ActorUserId, auditEvent.ActorUserId);
        Assert.Null(auditEvent.RequestedCompanyId);
        Assert.Equal(factory.CorrelationId, auditEvent.CorrelationId);
        Assert.Equal(AuditOutcome.Denied, auditEvent.Outcome);
    }

    [Fact]
    public async Task Capability_map_without_membership_returns_the_stable_membership_error()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var companyId = await SeedCompanyAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/foundation/capabilities?companyId={companyId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("membership_required", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task Capability_map_without_a_grant_returns_the_stable_capability_error()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var companyId = await SeedMembershipAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/foundation/capabilities?companyId={companyId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("capability_denied", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task Capability_map_returns_distinct_scopes_grouped_by_capability_name()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var companyId = await SeedGrantedMembershipAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/foundation/capabilities?companyId={companyId}");

        response.EnsureSuccessStatusCode();
        var capabilities = await response.Content.ReadFromJsonAsync<List<CapabilityResponse>>();
        var capability = Assert.Single(capabilities!);
        Assert.Equal("foundation.roles.assign", capability.Name);
        Assert.Equal(["Company", "Team"], capability.Scopes.OrderBy(scope => scope));
    }

    [Theory]
    [InlineData(null, "00000000-0000-0000-0000-000000000001")]
    [InlineData("not-a-user-id", "00000000-0000-0000-0000-000000000001")]
    [InlineData("00000000-0000-0000-0000-000000000001", null)]
    [InlineData("00000000-0000-0000-0000-000000000001", "not-a-correlation-id")]
    public async Task Capability_map_with_missing_or_malformed_actor_claims_returns_generic_unauthorized(
        string? userIdClaim,
        string? correlationIdClaim)
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(userIdClaim, correlationIdClaim);
        var response = await factory.CreateClient().GetAsync("/foundation/capabilities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> SeedCompanyAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Obras", factory.ActorUserId, requestId);
        administrator.AddRange(group, company);
        await administrator.SaveChangesAsync();
        return company.Id;
    }

    private static async Task<Guid> SeedMembershipAsync(FoundationApiFactory factory)
    {
        var companyId = await SeedCompanyAsync(factory);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var actor = new User(factory.ActorUserId, "Ana", "ana@example.test");
        administrator.AddRange(actor, new Membership(
            Guid.NewGuid(),
            companyId,
            factory.ActorUserId,
            factory.ActorUserId,
            Guid.NewGuid()));
        await administrator.SaveChangesAsync();
        return companyId;
    }

    private static async Task<Guid> SeedGrantedMembershipAsync(FoundationApiFactory factory)
    {
        var companyId = await SeedMembershipAsync(factory);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var actorMembership = await administrator.Memberships.SingleAsync();
        var role = new Role(Guid.NewGuid(), companyId, "Foundation administrator", factory.ActorUserId, Guid.NewGuid());
        var capability = new Capability(Guid.NewGuid(), "foundation.roles.assign");
        var companyGrant = new RoleCapability(
            Guid.NewGuid(), companyId, role.Id, capability.Id, CapabilityScope.Company, factory.ActorUserId, Guid.NewGuid());
        var teamGrant = new RoleCapability(
            Guid.NewGuid(), companyId, role.Id, capability.Id, CapabilityScope.Team, factory.ActorUserId, Guid.NewGuid());
        var assignment = new RoleAssignment(
            Guid.NewGuid(), companyId, role.Id, actorMembership.Id, factory.ActorUserId, Guid.NewGuid());
        administrator.AddRange(role, capability, companyGrant, teamGrant, assignment);
        await administrator.SaveChangesAsync();
        return companyId;
    }

    private sealed record ErrorResponse(string Code);
    private sealed record CapabilityResponse(string Name, IReadOnlyList<string> Scopes);
}
