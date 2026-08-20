using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>Contrato RED da reserva local de funcionário, sem login nem convite.</summary>
public sealed class EmployeeDirectoryEndpointTests
{
    private const string CreateCapability = "foundation.employees.create";
    private static readonly string[] sourceArray = new[] { "employeeId", "membershipId", "userId" };

    [Fact]
    public async Task Create_creates_local_user_membership_and_employee_without_external_access()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync(
                $"/foundation/companies/{scenario.CompanyId}/employees",
                NewRequest(scenario.CompanyId).request
            );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            1,
            await administrator.Employees.CountAsync(employee =>
                employee.CompanyId == scenario.CompanyId
            )
        );
        Assert.Equal(2, await administrator.Users.CountAsync());
        Assert.Equal(
            2,
            await administrator.Memberships.CountAsync(membership =>
                membership.CompanyId == scenario.CompanyId
            )
        );
        Assert.Empty(
            await administrator
                .ExternalIdentities.Where(identity => identity.UserId != factory.ActorUserId)
                .ToListAsync()
        );
        Assert.Empty(
            await administrator
                .RoleAssignments.Where(assignment =>
                    assignment.MembershipId != scenario.ActorMembershipId
                )
                .ToListAsync()
        );
        var reservedUserId = await administrator
            .Employees.Where(employee => employee.CompanyId == scenario.CompanyId)
            .Select(employee => employee.Membership.UserId)
            .SingleAsync();
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var invitation = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM foundation.pending_owner_invitations WHERE user_id = @user_id",
            connection
        );
        invitation.Parameters.AddWithValue("user_id", reservedUserId);
        Assert.Equal(0L, (long)(await invitation.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Create_replays_same_request_without_duplicate_rows()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory);
        var request = NewRequest(scenario.CompanyId);
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync(
            $"/foundation/companies/{scenario.CompanyId}/employees",
            request.request
        );
        using var replay = await client.PostAsJsonAsync(
            $"/foundation/companies/{scenario.CompanyId}/employees",
            request.request
        );

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var firstResult = await ReadOpaqueEmployeeResponseAsync(first);
        var replayResult = await ReadOpaqueEmployeeResponseAsync(replay);
        Assert.Equal(firstResult, replayResult);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            1,
            await administrator.Employees.CountAsync(employee =>
                employee.CompanyId == scenario.CompanyId
            )
        );
        await AssertOneSuccessAuditAsync(administrator, scenario.CompanyId, firstResult.EmployeeId);
    }

    [Fact]
    public async Task Create_handles_concurrent_identical_requests_with_one_local_graph_and_one_success_audit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory);
        var request = NewRequest(scenario.CompanyId);

        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(async _ =>
                {
                    using var client = factory.CreateClient();
                    using var response = await client.PostAsJsonAsync(
                        $"/foundation/companies/{scenario.CompanyId}/employees",
                        request.request
                    );
                    return new WriteResponse(
                        response.StatusCode,
                        await response.Content.ReadAsStringAsync()
                    );
                })
        );

        Assert.Single(results, result => result.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, results.Count(result => result.StatusCode == HttpStatusCode.OK));
        var responses = results.Select(result => ReadOpaqueEmployeeResponse(result.Body)).ToArray();
        Assert.Single(responses.Distinct());

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            1,
            await administrator.Employees.CountAsync(employee =>
                employee.CompanyId == scenario.CompanyId
            )
        );
        Assert.Equal(
            1,
            await administrator.Users.CountAsync(user => user.Email == "employee@example.test")
        );
        Assert.Equal(
            1,
            await administrator.Memberships.CountAsync(membership =>
                membership.CompanyId == scenario.CompanyId
                && membership.User.Email == "employee@example.test"
            )
        );
        await AssertOneSuccessAuditAsync(
            administrator,
            scenario.CompanyId,
            responses[0].EmployeeId
        );
    }

    [Fact]
    public async Task Create_rejects_same_request_id_with_changed_payload()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory);
        var requestId = Guid.NewGuid();
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync(
            $"/foundation/companies/{scenario.CompanyId}/employees",
            NewRequest(scenario.CompanyId, requestId, "Ana").request
        );
        using var conflict = await client.PostAsJsonAsync(
            $"/foundation/companies/{scenario.CompanyId}/employees",
            NewRequest(scenario.CompanyId, requestId, "Bia").request
        );

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "{\"code\":\"idempotency_conflict\"}",
            await conflict.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task Create_requires_authenticated_known_identity()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(null, null);
        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync(
                $"/foundation/companies/{Guid.NewGuid()}/employees",
                NewRequest(Guid.NewGuid()).request
            );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_strict_json_contract_violations_before_the_mutation()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory);
        using var client = factory.CreateClient();

        using var nonJson = await client.PostAsync(
            $"/foundation/companies/{scenario.CompanyId}/employees",
            new StringContent("{}", System.Text.Encoding.UTF8, "text/plain")
        );
        using var unknownField = await client.PostAsJsonAsync(
            $"/foundation/companies/{scenario.CompanyId}/employees",
            new
            {
                requestId = Guid.NewGuid(),
                companyId = scenario.CompanyId,
                displayName = "Employee",
                email = "employee@example.test",
                role = "owner",
            }
        );
        using var missingField = await client.PostAsJsonAsync(
            $"/foundation/companies/{scenario.CompanyId}/employees",
            new
            {
                requestId = Guid.NewGuid(),
                companyId = scenario.CompanyId,
                displayName = "Employee",
            }
        );
        using var malformedJson = await client.PostAsync(
            $"/foundation/companies/{scenario.CompanyId}/employees",
            new StringContent("{\"requestId\":", System.Text.Encoding.UTF8, "application/json")
        );
        var duplicateRequestId = Guid.NewGuid();
        using var duplicateProperty = await client.PostAsync(
            $"/foundation/companies/{scenario.CompanyId}/employees",
            new StringContent(
                $$"""
                {
                  "requestId":"{{duplicateRequestId}}",
                  "requestId":"{{Guid.NewGuid()}}",
                  "companyId":"{{scenario.CompanyId}}",
                  "displayName":"Employee",
                  "email":"employee@example.test"
                }
                """,
                System.Text.Encoding.UTF8,
                "application/json"
            )
        );
        using var routeBodyMismatch = await client.PostAsJsonAsync(
            $"/foundation/companies/{scenario.CompanyId}/employees",
            NewRequest(Guid.NewGuid()).request
        );

        foreach (
            var response in new[]
            {
                nonJson,
                unknownField,
                missingField,
                malformedJson,
                duplicateProperty,
                routeBodyMismatch,
            }
        )
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                "{\"code\":\"invalid_request\"}",
                await response.Content.ReadAsStringAsync()
            );
        }
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(
            await administrator
                .Employees.Where(employee => employee.CompanyId == scenario.CompanyId)
                .ToListAsync()
        );
    }

    [Fact]
    public async Task Create_rejects_company_not_owned_by_actor_membership()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAuthorizedCompanyAsync(factory);
        var otherCompany = await SeedCompanyAsync(factory, "Other Company");

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync(
                $"/foundation/companies/{otherCompany.Id}/employees",
                NewRequest(otherCompany.Id).request
            );

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_actor_without_create_capability()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory, grantCreate: false);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync(
                $"/foundation/companies/{scenario.CompanyId}/employees",
                NewRequest(scenario.CompanyId).request
            );

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_duplicate_email_without_partial_rows_or_audit_success()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory);
        var request = NewRequest(scenario.CompanyId, email: "same-address@example.test").request;

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync($"/foundation/companies/{scenario.CompanyId}/employees", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator.Users.CountAsync());
        Assert.Empty(
            await administrator
                .Employees.Where(employee => employee.CompanyId == scenario.CompanyId)
                .ToListAsync()
        );
        Assert.Empty(
            await administrator
                .AuditEvents.Where(audit =>
                    audit.Action == "foundation.employee.create"
                    && audit.Outcome == AuditOutcome.Success
                )
                .ToListAsync()
        );
    }

    [Fact]
    public async Task Create_rejects_syntactically_invalid_email_without_persisting_rows()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync(
                $"/foundation/companies/{scenario.CompanyId}/employees",
                NewRequest(scenario.CompanyId, email: "not-an-email").request
            );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"code\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(
            await administrator
                .Employees.Where(item => item.CompanyId == scenario.CompanyId)
                .ToListAsync()
        );
        Assert.Empty(
            await administrator.Users.Where(item => item.Email == "not-an-email").ToListAsync()
        );
    }

    [Fact]
    public async Task Create_rejects_oversized_and_chunked_json_before_persisting_rows()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory);
        using var client = factory.CreateClient();
        var route = $"/foundation/companies/{scenario.CompanyId}/employees";
        var oversized =
            new string(' ', 40000)
            + $$"""
                {"requestId":"{{Guid.NewGuid()}}","companyId":"{{scenario.CompanyId}}","displayName":"Employee","email":"employee@example.test"}
                """;

        using var oversizedResponse = await client.PostAsync(
            route,
            new StringContent(oversized, Encoding.UTF8, "application/json")
        );
        using var chunkedRequest = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(oversized))),
        };
        chunkedRequest.Headers.TransferEncodingChunked = true;
        chunkedRequest.Content.Headers.ContentType = new("application/json");
        using var chunkedResponse = await client.SendAsync(chunkedRequest);

        foreach (var response in new[] { oversizedResponse, chunkedResponse })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                "{\"code\":\"invalid_request\"}",
                await response.Content.ReadAsStringAsync()
            );
        }
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(
            await administrator
                .Employees.Where(item => item.CompanyId == scenario.CompanyId)
                .ToListAsync()
        );
    }

    [Fact]
    public async Task Directory_responses_are_private_and_not_stored_for_authenticated_success_and_error()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory);
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(
            $"/foundation/companies/{scenario.CompanyId}/employees",
            NewRequest(scenario.CompanyId).request
        );
        using var listed = await client.GetAsync(
            $"/foundation/companies/{scenario.CompanyId}/employees"
        );
        var deniedScenario = await SeedAuthorizedCompanyAsync(
            factory,
            "Denied",
            grantCreate: false
        );
        using var denied = await client.GetAsync(
            $"/foundation/companies/{deniedScenario.CompanyId}/employees"
        );

        foreach (var response in new[] { created, listed, denied })
        {
            Assert.True(response.Headers.CacheControl?.Private);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task Directory_returns_only_employees_from_requested_company()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var first = await SeedAuthorizedCompanyAsync(factory);
        var second = await SeedAuthorizedCompanyAsync(factory, "Second Company");
        var client = factory.CreateClient();
        await client.PostAsJsonAsync(
            $"/foundation/companies/{first.CompanyId}/employees",
            NewRequest(first.CompanyId, displayName: "First").request
        );
        await client.PostAsJsonAsync(
            $"/foundation/companies/{second.CompanyId}/employees",
            NewRequest(second.CompanyId, displayName: "Second").request
        );

        using var response = await client.GetAsync(
            $"/foundation/companies/{first.CompanyId}/employees"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("First", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Second", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Directory_requires_authentication_without_returning_employee_data()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(null, null);
        using var response = await factory
            .CreateClient()
            .GetAsync($"/foundation/companies/{Guid.NewGuid()}/employees");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Directory_unauthorized_and_identity_gate_failure_responses_are_private_and_not_stored()
    {
        var route = $"/foundation/companies/{Guid.NewGuid()}/employees";

        await using var anonymousFactory = await FoundationApiFactory.CreateWithClaimsAsync(
            null,
            null
        );
        using var unauthorized = await anonymousFactory.CreateClient().GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.True(unauthorized.Headers.CacheControl?.Private);
        Assert.True(unauthorized.Headers.CacheControl?.NoStore);

        await using var unavailableFactory =
            await FoundationApiFactory.CreateWithJwtBearerAndDatabaseFailureAsync(
                GateDatabaseFailurePoint.Resolution
            );
        using var unavailableClient = unavailableFactory.CreateClient();
        unavailableClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            unavailableFactory.CreateJwtToken(JwtTokenVariation.Valid)
        );
        using var unavailable = await unavailableClient.GetAsync(route);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.True(unavailable.Headers.CacheControl?.Private);
        Assert.True(unavailable.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Directory_rejects_an_actor_without_the_create_capability_without_leaking_rows()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory, grantCreate: false);
        await SeedEmployeeAsync(
            factory,
            scenario.CompanyId,
            "Hidden employee",
            "hidden@example.test"
        );

        using var response = await factory
            .CreateClient()
            .GetAsync($"/foundation/companies/{scenario.CompanyId}/employees");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("{\"code\":\"capability_denied\"}", body);
        Assert.DoesNotContain("Hidden employee", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Directory_rejects_an_authorized_actor_for_another_company_without_leaking_rows()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAuthorizedCompanyAsync(factory);
        var otherCompany = await SeedCompanyAsync(factory, "Other Company");
        await SeedEmployeeAsync(
            factory,
            otherCompany.Id,
            "Other tenant employee",
            "other@example.test"
        );

        using var response = await factory
            .CreateClient()
            .GetAsync($"/foundation/companies/{otherCompany.Id}/employees");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("{\"code\":\"capability_denied\"}", body);
        Assert.DoesNotContain("Other tenant employee", body, StringComparison.Ordinal);
    }

    private static (object request, Guid RequestId) NewRequest(
        Guid companyId,
        Guid? requestId = null,
        string displayName = "Employee",
        string email = "employee@example.test"
    )
    {
        var id = requestId ?? Guid.NewGuid();
        return (
            new
            {
                requestId = id,
                companyId,
                displayName,
                email,
            },
            id
        );
    }

    private static async Task<Scenario> SeedAuthorizedCompanyAsync(
        FoundationApiFactory factory,
        string companyName = "Company",
        bool grantCreate = true
    )
    {
        var company = await SeedCompanyAsync(factory, companyName);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var actorMembership = new Membership(
            Guid.NewGuid(),
            company.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            Guid.NewGuid()
        );
        var role = new Role(
            Guid.NewGuid(),
            company.Id,
            "Company owner",
            factory.ActorUserId,
            Guid.NewGuid()
        );
        var assignment = new RoleAssignment(
            Guid.NewGuid(),
            company.Id,
            role.Id,
            actorMembership.Id,
            factory.ActorUserId,
            Guid.NewGuid()
        );
        administrator.AddRange(actorMembership, role, assignment);
        if (grantCreate)
        {
            var capability = await administrator.Capabilities.SingleOrDefaultAsync(candidate =>
                candidate.Name == CreateCapability
            );
            if (capability is null)
            {
                capability = new Capability(Guid.NewGuid(), CreateCapability);
                administrator.Add(capability);
            }
            administrator.Add(
                new RoleCapability(
                    Guid.NewGuid(),
                    company.Id,
                    role.Id,
                    capability.Id,
                    CapabilityScope.Company,
                    factory.ActorUserId,
                    Guid.NewGuid()
                )
            );
        }
        await administrator.SaveChangesAsync();
        return new Scenario(company.Id, actorMembership.Id);
    }

    private static async Task<Company> SeedCompanyAsync(FoundationApiFactory factory, string name)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var group = new Group(Guid.NewGuid(), $"Group {name}", factory.ActorUserId, Guid.NewGuid());
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            name,
            factory.ActorUserId,
            Guid.NewGuid()
        );
        administrator.AddRange(group, company);
        await administrator.SaveChangesAsync();
        return company;
    }

    private static async Task SeedEmployeeAsync(
        FoundationApiFactory factory,
        Guid companyId,
        string displayName,
        string email
    )
    {
        var userId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var user = new User(userId, displayName, email);
        var membership = new Membership(
            Guid.NewGuid(),
            companyId,
            user.Id,
            factory.ActorUserId,
            requestId
        );
        administrator.AddRange(
            user,
            membership,
            new Employee(
                Guid.NewGuid(),
                companyId,
                membership.Id,
                displayName,
                factory.ActorUserId,
                requestId
            )
        );
        await administrator.SaveChangesAsync();
    }

    private static async Task<OpaqueEmployeeResponse> ReadOpaqueEmployeeResponseAsync(
        HttpResponseMessage response
    )
    {
        return ReadOpaqueEmployeeResponse(await response.Content.ReadAsStringAsync());
    }

    private static OpaqueEmployeeResponse ReadOpaqueEmployeeResponse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(
sourceArray.Order(),
            root.EnumerateObject().Select(property => property.Name).Order()
        );
        foreach (
            var forbidden in new[] { "email", "company", "role", "identity", "invite", "token" }
        )
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        return new OpaqueEmployeeResponse(
            root.GetProperty("employeeId").GetGuid(),
            root.GetProperty("membershipId").GetGuid(),
            root.GetProperty("userId").GetGuid()
        );
    }

    private static async Task AssertOneSuccessAuditAsync(
        FoundationDbContext administrator,
        Guid companyId,
        Guid employeeId
    )
    {
        var audit = Assert.Single(
            await administrator
                .AuditEvents.Where(item =>
                    item.Action == "foundation.employee.create"
                    && item.Outcome == AuditOutcome.Success
                )
                .ToListAsync()
        );
        Assert.Equal(AuditScope.Company, audit.Scope);
        Assert.Equal(companyId, audit.RequestedCompanyId);
        Assert.Equal("employee", audit.ResourceType);
        Assert.Equal(employeeId, audit.ResourceId);
    }

    private sealed record Scenario(Guid CompanyId, Guid ActorMembershipId);

    private sealed record OpaqueEmployeeResponse(Guid EmployeeId, Guid MembershipId, Guid UserId);

    private sealed record WriteResponse(HttpStatusCode StatusCode, string Body);
}
