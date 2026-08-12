using System.Net;
using System.Net.Http.Json;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class MembershipMapEndpointTests
{
    [Fact]
    public async Task Membership_map_returns_only_the_current_actor_memberships_in_name_order_without_grants()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seeded = await SeedMembershipsAsync(factory);

        var response = await factory.CreateClient().GetAsync("/foundation/memberships");

        response.EnsureSuccessStatusCode();
        var memberships = await response.Content.ReadFromJsonAsync<List<MembershipResponse>>()
            ?? throw new InvalidOperationException("The membership response body was empty.");
        Assert.Equal(["Alfa", "Beta"], memberships.Select(membership => membership.CompanyName));
        Assert.Equal(
            [seeded.AlfaMembershipId, seeded.BetaMembershipId],
            memberships.Select(membership => membership.MembershipId));
        Assert.All(memberships, membership => Assert.Empty(membership.Capabilities));
    }

    [Fact]
    public async Task Membership_map_rejects_an_anonymous_call()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(null, null);

        var response = await factory.CreateClient().GetAsync("/foundation/memberships");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<MembershipMapSeed> SeedMembershipsAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var otherUser = new User(Guid.NewGuid(), "Bea", "bea@example.test");
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var alfa = new Company(Guid.NewGuid(), group.Id, "Alfa", factory.ActorUserId, requestId);
        var beta = new Company(Guid.NewGuid(), group.Id, "Beta", factory.ActorUserId, requestId);
        var otherCompany = new Company(
            Guid.NewGuid(), group.Id, "Outra", factory.ActorUserId, requestId);
        var alfaMembership = new Membership(
            Guid.NewGuid(), alfa.Id, factory.ActorUserId, factory.ActorUserId, requestId);
        var betaMembership = new Membership(
            Guid.NewGuid(), beta.Id, factory.ActorUserId, factory.ActorUserId, requestId);
        var otherMembership = new Membership(
            Guid.NewGuid(), otherCompany.Id, otherUser.Id, factory.ActorUserId, requestId);

        administrator.AddRange(
            otherUser,
            group,
            alfa,
            beta,
            otherCompany,
            alfaMembership,
            betaMembership,
            otherMembership);
        await administrator.SaveChangesAsync();

        return new MembershipMapSeed(alfaMembership.Id, betaMembership.Id);
    }

    private sealed record MembershipMapSeed(Guid AlfaMembershipId, Guid BetaMembershipId);

    private sealed record MembershipResponse(
        Guid MembershipId,
        Guid CompanyId,
        string CompanyName,
        IReadOnlyList<CapabilityResponse> Capabilities);

    private sealed record CapabilityResponse(string Name, IReadOnlyList<string> Scopes);
}
