using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class MembershipMapEndpointTests
{
    [Fact]
    public async Task Membership_map_returns_only_the_current_actor_memberships_with_company_scoped_grants()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seeded = await SeedMembershipMapAsync(factory);

        var response = await factory.CreateClient().GetAsync("/foundation/memberships");

        response.EnsureSuccessStatusCode();
        var memberships =
            await response.Content.ReadFromJsonAsync<List<MembershipResponse>>()
            ?? throw new InvalidOperationException("The membership response body was empty.");
        Assert.Equal(
            ["Alfa", "Beta", "Gama"],
            memberships.Select(membership => membership.CompanyName)
        );
        Assert.Equal(
            [seeded.AlfaMembershipId, seeded.BetaMembershipId, seeded.GamaMembershipId],
            memberships.Select(membership => membership.MembershipId)
        );
        Assert.Equal(seeded.AlfaCompanyId, memberships[0].CompanyId);
        Assert.Equal(seeded.BetaCompanyId, memberships[1].CompanyId);
        Assert.Equal(seeded.GamaCompanyId, memberships[2].CompanyId);
        Assert.Equal("payment.approve", Assert.Single(memberships[0].Capabilities).Name);
        Assert.Equal(["Company"], Assert.Single(memberships[0].Capabilities).Scopes);
        Assert.Equal("foundation.files.upload", Assert.Single(memberships[1].Capabilities).Name);
        Assert.Equal(["Company"], Assert.Single(memberships[1].Capabilities).Scopes);
        Assert.Empty(memberships[2].Capabilities);

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("role", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("issuer", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subject", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain", responseBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Membership_map_rejects_an_anonymous_call()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(null, null);

        var response = await factory.CreateClient().GetAsync("/foundation/memberships");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<MembershipMapSeed> SeedMembershipMapAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var otherUser = new User(Guid.NewGuid(), "Bea", "bea@example.test");
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var alfa = new Company(Guid.NewGuid(), group.Id, "Alfa", factory.ActorUserId, requestId);
        var beta = new Company(Guid.NewGuid(), group.Id, "Beta", factory.ActorUserId, requestId);
        var gama = new Company(Guid.NewGuid(), group.Id, "Gama", factory.ActorUserId, requestId);
        var otherCompany = new Company(
            Guid.NewGuid(),
            group.Id,
            "Outra",
            factory.ActorUserId,
            requestId
        );
        var alfaMembership = new Membership(
            Guid.NewGuid(),
            alfa.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            requestId
        );
        var betaMembership = new Membership(
            Guid.NewGuid(),
            beta.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            requestId
        );
        var gamaMembership = new Membership(
            Guid.NewGuid(),
            gama.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            requestId
        );
        var otherMembership = new Membership(
            Guid.NewGuid(),
            otherCompany.Id,
            otherUser.Id,
            factory.ActorUserId,
            requestId
        );
        var alfaRole = new Role(
            Guid.NewGuid(),
            alfa.Id,
            "Alfa approver",
            factory.ActorUserId,
            requestId
        );
        var betaRole = new Role(
            Guid.NewGuid(),
            beta.Id,
            "Beta uploader",
            factory.ActorUserId,
            requestId
        );
        var paymentApprove = new Capability(Guid.NewGuid(), "payment.approve");
        var filesUpload = await administrator.Capabilities.SingleAsync(capability =>
            capability.Name == KnownCapabilities.FilesUpload
        );

        administrator.AddRange(
            otherUser,
            paymentApprove,
            group,
            alfa,
            beta,
            gama,
            otherCompany,
            alfaMembership,
            betaMembership,
            gamaMembership,
            otherMembership,
            alfaRole,
            betaRole,
            new RoleAssignment(
                Guid.NewGuid(),
                alfa.Id,
                alfaRole.Id,
                alfaMembership.Id,
                factory.ActorUserId,
                requestId
            ),
            new RoleAssignment(
                Guid.NewGuid(),
                beta.Id,
                betaRole.Id,
                betaMembership.Id,
                factory.ActorUserId,
                requestId
            ),
            new RoleCapability(
                Guid.NewGuid(),
                alfa.Id,
                alfaRole.Id,
                paymentApprove.Id,
                CapabilityScope.Company,
                factory.ActorUserId,
                requestId
            ),
            new RoleCapability(
                Guid.NewGuid(),
                beta.Id,
                betaRole.Id,
                filesUpload.Id,
                CapabilityScope.Company,
                factory.ActorUserId,
                requestId
            )
        );
        await administrator.SaveChangesAsync();

        return new MembershipMapSeed(
            alfaMembership.Id,
            betaMembership.Id,
            gamaMembership.Id,
            alfa.Id,
            beta.Id,
            gama.Id
        );
    }

    private sealed record MembershipMapSeed(
        Guid AlfaMembershipId,
        Guid BetaMembershipId,
        Guid GamaMembershipId,
        Guid AlfaCompanyId,
        Guid BetaCompanyId,
        Guid GamaCompanyId
    );

    private sealed record MembershipResponse(
        Guid MembershipId,
        Guid CompanyId,
        string CompanyName,
        IReadOnlyList<CapabilityResponse> Capabilities
    );

    private sealed record CapabilityResponse(string Name, IReadOnlyList<string> Scopes);
}
