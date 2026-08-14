using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AuthorizationEndpointTests
{
    [Fact]
    public async Task Capability_map_uses_server_correlation_instead_of_a_client_claim()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/foundation/capabilities");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("company_context_required", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var auditEvents = await administrator.AuditEvents.OrderBy(auditEvent => auditEvent.Action).ToListAsync();
        var admission = Assert.Single(auditEvents, auditEvent => auditEvent.Action == "identity_session_admitted");
        var auditEvent = Assert.Single(auditEvents, auditEvent => auditEvent.Action == "company_context_required");
        Assert.Equal(factory.ActorUserId, admission.ActorUserId);
        Assert.Equal(AuditSource.Web, admission.Source);
        Assert.Equal(factory.ActorUserId, auditEvent.ActorUserId);
        Assert.Null(auditEvent.RequestedCompanyId);
        Assert.NotEqual(Guid.Empty, auditEvent.CorrelationId);
        Assert.NotEqual(factory.CorrelationId, auditEvent.CorrelationId);
        Assert.Equal(AuditOutcome.Denied, auditEvent.Outcome);
    }

    [Fact]
    public async Task Capability_map_without_membership_returns_the_stable_membership_error()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await SeedCompanyAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/foundation/capabilities?companyId={companyId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("membership_required", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task Capability_map_without_a_grant_returns_the_stable_capability_error()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await SeedMembershipAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/foundation/capabilities?companyId={companyId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("capability_denied", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task Capability_map_returns_distinct_scopes_grouped_by_capability_name()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await SeedGrantedMembershipAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/foundation/capabilities?companyId={companyId}");

        response.EnsureSuccessStatusCode();
        var capabilities = await response.Content.ReadFromJsonAsync<List<CapabilityResponse>>();
        var capability = Assert.Single(capabilities!);
        Assert.Equal("foundation.roles.assign", capability.Name);
        Assert.Equal(["Company", "Team"], capability.Scopes.OrderBy(scope => scope));
    }

    [Fact]
    public async Task Capability_map_uses_the_internal_user_mapped_from_issuer_and_subject()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await SeedGrantedMembershipAsync(factory);

        var response = await factory.CreateClient().GetAsync($"/foundation/capabilities?companyId={companyId}");

        response.EnsureSuccessStatusCode();
        var capability = Assert.Single(await response.Content.ReadFromJsonAsync<List<CapabilityResponse>>() ?? []);
        Assert.Equal("foundation.roles.assign", capability.Name);
    }

    [Fact]
    public async Task Capability_map_rejects_an_unknown_external_identity_without_using_email()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(
            "https://project.supabase.co/auth/v1",
            "unknown-subject");
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        administrator.Users.Add(new User(factory.ActorUserId, "Ana", "same-address@example.test"));
        await administrator.SaveChangesAsync();

        var response = await factory.CreateClient().GetAsync("/foundation/capabilities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, await administrator.Users.CountAsync());
        Assert.Empty(await administrator.ExternalIdentities.ToListAsync());
    }

    [Fact]
    public async Task Capability_map_rejects_an_identity_from_another_issuer()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(
            "https://another-project.supabase.co/auth/v1",
            "external-subject");
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        administrator.AddRange(
            new User(factory.ActorUserId, "Ana", "same-address@example.test"),
            new ExternalIdentity(
                Guid.NewGuid(),
                factory.ActorUserId,
                "https://project.supabase.co/auth/v1",
                "external-subject"));
        await administrator.SaveChangesAsync();

        var response = await factory.CreateClient().GetAsync("/foundation/capabilities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Runtime_can_read_external_identity_lookup_without_mutating_it()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = administrator.CreateCommand();
        command.CommandText =
            """
            SELECT
                has_table_privilege('vinx_runtime', 'foundation.external_identities', 'SELECT'),
                has_table_privilege('vinx_runtime', 'foundation.external_identities', 'INSERT'),
                has_table_privilege('vinx_runtime', 'foundation.external_identities', 'UPDATE'),
                has_table_privilege('vinx_runtime', 'foundation.external_identities', 'DELETE')
            """;
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
    }

    [Fact]
    public async Task Jwt_bearer_materializes_each_amr_array_item_as_a_json_claim_without_its_container()
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.CreateJwtToken(JwtTokenVariation.TwoAmrEntries));

        var response = await client.GetAsync("/foundation/capabilities");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var amrClaims = factory.AuthenticatedJwtPrincipal?.FindAll("amr").ToList();
        Assert.NotNull(amrClaims);
        Assert.Equal(2, amrClaims.Count);
        var materializedAmr = amrClaims.Select(claim =>
        {
            Assert.Equal(JsonClaimValueTypes.Json, claim.ValueType);
            using var document = System.Text.Json.JsonDocument.Parse(claim.Value);
            Assert.Equal(System.Text.Json.JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.Equal(2, document.RootElement.EnumerateObject().Count());
            Assert.True(document.RootElement.TryGetProperty("method", out var method));
            Assert.Equal(System.Text.Json.JsonValueKind.String, method.ValueKind);
            Assert.True(document.RootElement.TryGetProperty("timestamp", out var timestamp));
            Assert.Equal(System.Text.Json.JsonValueKind.Number, timestamp.ValueKind);
            Assert.False(document.RootElement.TryGetProperty("Method", out _));
            Assert.False(document.RootElement.TryGetProperty("Timestamp", out _));
            return (Method: method.GetString(), Timestamp: timestamp.GetInt64());
        }).ToList();
        var issuedAt = long.Parse(factory.AuthenticatedJwtPrincipal!.FindFirst("iat")!.Value);
        Assert.Equal(["password", "email"], materializedAmr.Select(entry => entry.Method));
        Assert.Equal([issuedAt - 10, issuedAt - 20], materializedAmr.Select(entry => entry.Timestamp));
        // JwtBearer removes the parent array, so this boundary cannot distinguish a top-level object from one array item.
    }

    [Fact]
    public async Task Capability_map_reaches_the_handler_for_a_valid_jwt_session_proof()
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAndCountingIdentityProviderAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.CreateJwtToken(JwtTokenVariation.Valid));

        var response = await client.GetAsync("/foundation/capabilities");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("company_context_required", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.Equal(1, factory.IdentityProviderCallCount);
    }

    [Theory]
    [InlineData(JwtTokenVariation.MissingSessionId)]
    [InlineData(JwtTokenVariation.MalformedSessionId)]
    [InlineData(JwtTokenVariation.MissingAmr)]
    [InlineData(JwtTokenVariation.EmptyAmr)]
    [InlineData(JwtTokenVariation.RefreshOnlyAmr)]
    [InlineData(JwtTokenVariation.AuthenticatorAfterIssuedAt)]
    [InlineData(JwtTokenVariation.IssuedAtAtOrAfterExpiresAt)]
    public async Task Capability_map_rejects_a_valid_jwt_without_session_proof(
        JwtTokenVariation variation)
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.CreateJwtToken(variation));

        var response = await client.GetAsync("/foundation/capabilities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(JwtTokenVariation.InvalidSignature)]
    [InlineData(JwtTokenVariation.InvalidIssuer)]
    [InlineData(JwtTokenVariation.InvalidAudience)]
    [InlineData(JwtTokenVariation.Expired)]
    public async Task Capability_map_rejects_jwt_bearer_tokens_that_fail_production_validation(
        JwtTokenVariation variation)
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAndCountingIdentityProviderAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.CreateJwtToken(variation));

        var response = await client.GetAsync("/foundation/capabilities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.IdentityProviderCallCount);
    }

    [Theory]
    [InlineData(null, "external-subject")]
    [InlineData("https://project.supabase.co/auth/v1", null)]
    public async Task Capability_map_rejects_missing_external_identity_claims(
        string? issuer,
        string? subject)
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(issuer, subject);
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
        administrator.Add(new Membership(
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
        var capability = await administrator.Capabilities.SingleAsync(candidate =>
            candidate.Name == KnownCapabilities.RolesAssign);
        var companyGrant = new RoleCapability(
            Guid.NewGuid(), companyId, role.Id, capability.Id, CapabilityScope.Company, factory.ActorUserId, Guid.NewGuid());
        var teamGrant = new RoleCapability(
            Guid.NewGuid(), companyId, role.Id, capability.Id, CapabilityScope.Team, factory.ActorUserId, Guid.NewGuid());
        var assignment = new RoleAssignment(
            Guid.NewGuid(), companyId, role.Id, actorMembership.Id, factory.ActorUserId, Guid.NewGuid());
        administrator.AddRange(role, companyGrant, teamGrant, assignment);
        await administrator.SaveChangesAsync();
        return companyId;
    }

    private sealed record ErrorResponse(string Code);
    private sealed record CapabilityResponse(string Name, IReadOnlyList<string> Scopes);
}
