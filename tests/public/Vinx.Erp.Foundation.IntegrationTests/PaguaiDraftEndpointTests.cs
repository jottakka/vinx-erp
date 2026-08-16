using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PaguaiDraftEndpointTests
{
    [Fact]
    public async Task Draft_creation_returns_a_company_scoped_rascunho()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var request = NewRequest(scenario.CompanyId, Guid.NewGuid());

        using var response = await client.PostAsJsonAsync("/paguai/requests", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.NotEqual(Guid.Empty, ReadGuid(root, "id"));
        Assert.Equal(scenario.CompanyId, ReadGuid(root, "companyId"));
        Assert.Equal(request.RequestId, ReadGuid(root, "requestId"));
        Assert.Equal("Office supplies", root.GetProperty("title").GetString());
        Assert.Equal("Replacement printer paper", root.GetProperty("description").GetString());
        Assert.Equal("rascunho", root.GetProperty("state").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Replaying_same_payload_returns_same_draft_and_different_payload_conflicts()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var request = NewRequest(scenario.CompanyId, Guid.NewGuid());

        using var first = await client.PostAsJsonAsync("/paguai/requests", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstDraftId = ReadGuid(firstDocument.RootElement, "id");

        using var replay = await client.PostAsJsonAsync("/paguai/requests", request);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(firstDraftId, ReadGuid(replayDocument.RootElement, "id"));
        Assert.Equal(
            "Office supplies",
            replayDocument.RootElement.GetProperty("title").GetString()
        );

        using var conflict = await client.PostAsJsonAsync(
            "/paguai/requests",
            request with
            {
                Title = "Different title",
            }
        );

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "{\"code\":\"idempotency_conflict\"}",
            await conflict.Content.ReadAsStringAsync()
        );

        using var list = await client.GetAsync($"/paguai/requests?companyId={scenario.CompanyId}");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listDocument = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var drafts = listDocument.RootElement.EnumerateArray().ToList();
        Assert.Equal(firstDraftId, ReadGuid(Assert.Single(drafts), "id"));
    }

    [Fact]
    public async Task Draft_reads_are_visible_to_member_company_only()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var request = NewRequest(scenario.CompanyId, Guid.NewGuid());

        using var create = await client.PostAsJsonAsync("/paguai/requests", request);

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var createDocument = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var draftId = ReadGuid(createDocument.RootElement, "id");

        using var detail = await client.GetAsync(
            $"/paguai/requests/{draftId}?companyId={scenario.CompanyId}"
        );

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var detailDocument = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal(draftId, ReadGuid(detailDocument.RootElement, "id"));
        Assert.Equal(
            "Office supplies",
            detailDocument.RootElement.GetProperty("title").GetString()
        );

        using var foreignDetail = await client.GetAsync(
            $"/paguai/requests/{draftId}?companyId={scenario.ForeignCompanyId}"
        );

        Assert.Equal(HttpStatusCode.NotFound, foreignDetail.StatusCode);
        Assert.Equal(
            "{\"code\":\"resource_not_found\"}",
            await foreignDetail.Content.ReadAsStringAsync()
        );

        using var foreignList = await client.GetAsync(
            $"/paguai/requests?companyId={scenario.ForeignCompanyId}"
        );
        var foreignListBody = await foreignList.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, foreignList.StatusCode);
        Assert.DoesNotContain("Office supplies", foreignListBody, StringComparison.Ordinal);
    }

    private static DraftRequest NewRequest(Guid companyId, Guid requestId) =>
        new(companyId, requestId, "Office supplies", "Replacement printer paper");

    private static Guid ReadGuid(JsonElement root, string propertyName)
    {
        Assert.True(root.TryGetProperty(propertyName, out var property));
        Assert.True(Guid.TryParse(property.GetString(), out var value));
        return value;
    }

    private static async Task<Scenario> SeedScenarioAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            "Vinx Obras",
            factory.ActorUserId,
            requestId
        );
        var foreignCompany = new Company(
            Guid.NewGuid(),
            group.Id,
            "Vinx Serviços",
            factory.ActorUserId,
            requestId
        );
        var membership = new Membership(
            Guid.NewGuid(),
            company.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            requestId
        );
        var role = new Role(
            Guid.NewGuid(),
            company.Id,
            "Paguai requester",
            factory.ActorUserId,
            requestId
        );
        var readCapability = await administrator.Capabilities.SingleOrDefaultAsync(capability =>
            capability.Name == "paguai.draft.read"
        );
        if (readCapability is null)
        {
            readCapability = new Capability(
                Guid.Parse("766bdb56-80c0-5dee-982f-be7f0d3eaea8"),
                "paguai.draft.read"
            );
            administrator.Capabilities.Add(readCapability);
        }

        var createCapability = await administrator.Capabilities.SingleOrDefaultAsync(capability =>
            capability.Name == "paguai.draft.create"
        );
        if (createCapability is null)
        {
            createCapability = new Capability(
                Guid.Parse("aecaa296-6a6f-5815-93ac-bd1ccdff3f44"),
                "paguai.draft.create"
            );
            administrator.Capabilities.Add(createCapability);
        }

        var readGrant = new RoleCapability(
            Guid.NewGuid(),
            company.Id,
            role.Id,
            readCapability.Id,
            CapabilityScope.Company,
            factory.ActorUserId,
            requestId
        );
        var createGrant = new RoleCapability(
            Guid.NewGuid(),
            company.Id,
            role.Id,
            createCapability.Id,
            CapabilityScope.Company,
            factory.ActorUserId,
            requestId
        );
        var assignment = new RoleAssignment(
            Guid.NewGuid(),
            company.Id,
            role.Id,
            membership.Id,
            factory.ActorUserId,
            requestId
        );

        administrator.AddRange(
            group,
            company,
            foreignCompany,
            membership,
            role,
            readGrant,
            createGrant,
            assignment
        );
        await administrator.SaveChangesAsync();

        return new Scenario(company.Id, foreignCompany.Id);
    }

    private sealed record DraftRequest(
        Guid CompanyId,
        Guid RequestId,
        string Title,
        string Description
    );

    private sealed record Scenario(Guid CompanyId, Guid ForeignCompanyId);
}
