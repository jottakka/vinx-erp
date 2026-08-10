using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class IdentityGateEndpointTests
{
    [Fact]
    public async Task Protected_capabilities_admits_a_valid_proof_once_and_uses_the_internal_actor()
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAndCountingIdentityProviderAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.CreateJwtToken(JwtTokenVariation.Valid));

        var first = await client.GetAsync("/foundation/capabilities");
        var second = await client.GetAsync("/foundation/capabilities");

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal(2, factory.IdentityProviderCallCount);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var session = await administrator.UserSessions.SingleAsync();
        var audits = await administrator.AuditEvents.ToListAsync();
        var audit = Assert.Single(audits, auditEvent => auditEvent.Action == "identity_session_admitted");
        Assert.Equal(factory.ActorUserId, session.UserId);
        Assert.Equal("identity_session_admitted", audit.Action);
        Assert.Equal(factory.ActorUserId, audit.ActorUserId);
        Assert.Equal(2, audits.Count(auditEvent => auditEvent.Action == "company_context_required"));
    }

    [Fact]
    public async Task Unknown_external_identity_returns_401_before_admission()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(
            "https://project.supabase.co/auth/v1",
            "unknown-subject");

        var response = await factory.CreateClient().GetAsync("/foundation/capabilities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.UserSessions.ToListAsync());
        Assert.Empty(await administrator.AuditEvents.ToListAsync());
    }

    [Theory]
    [InlineData(JwtTokenVariation.MissingSessionId)]
    [InlineData(JwtTokenVariation.RefreshOnlyAmr)]
    public async Task Invalid_session_proof_returns_401_without_a_reason(JwtTokenVariation variation)
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

    [Fact]
    public async Task Revoked_session_returns_401_before_capabilities_handler()
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAsync();
        var token = factory.CreateJwtToken(JwtTokenVariation.Valid);
        await SeedSessionAsync(factory, ProofFrom(token), revoke: true);
        using var client = AuthorizedClient(factory, token);

        var response = await client.GetAsync("/foundation/capabilities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Proof_at_or_before_session_barrier_returns_401_before_capabilities_handler(
        int barrierOffsetSeconds)
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAsync();
        var token = factory.CreateJwtToken(JwtTokenVariation.Valid);
        var proof = ProofFrom(token);
        await SetBarrierAsync(factory, proof.ProviderAuthenticatedAt.AddSeconds(barrierOffsetSeconds));
        using var client = AuthorizedClient(factory, token);

        var response = await client.GetAsync("/foundation/capabilities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Resolution_database_failure_returns_only_the_safe_identity_gate_code()
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAndDatabaseFailureAsync(
            GateDatabaseFailurePoint.Resolution);
        using var client = AuthorizedClient(factory, factory.CreateJwtToken(JwtTokenVariation.Valid));

        var response = await client.GetAsync("/foundation/capabilities");

        await AssertOnlyUnavailableCodeAsync(response);
    }

    [Fact]
    public async Task Admission_database_failure_returns_only_the_safe_identity_gate_code()
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAsync();
        await RevokeSessionAdmissionExecuteAsync(factory);
        using var client = AuthorizedClient(factory, factory.CreateJwtToken(JwtTokenVariation.Valid));

        var response = await client.GetAsync("/foundation/capabilities");

        await AssertOnlyUnavailableCodeAsync(response);
    }

    private static async Task AssertOnlyUnavailableCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("code", property.Name);
        Assert.Equal("identity_gate_unavailable", property.Value.GetString());
    }

    [Fact]
    public async Task Health_does_not_consult_the_identity_provider_or_session_gate()
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAndCountingIdentityProviderAsync();
        using var client = AuthorizedClient(factory, factory.CreateJwtToken(JwtTokenVariation.Valid));

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.Equal(0, factory.IdentityProviderCallCount);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.UserSessions.ToListAsync());
        Assert.Empty(await administrator.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Allow_anonymous_endpoint_skips_identity_provider_and_session_gate()
    {
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAndCountingIdentityProviderAsync();
        using var client = AuthorizedClient(factory, factory.CreateJwtToken(JwtTokenVariation.Valid));

        var response = await client.GetAsync("/test/identity-gate/allow-anonymous");

        response.EnsureSuccessStatusCode();
        Assert.Equal("Anonymous", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.IdentityProviderCallCount);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.UserSessions.ToListAsync());
        Assert.Empty(await administrator.AuditEvents.ToListAsync());
    }

    private static HttpClient AuthorizedClient(FoundationApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static ExternalIdentityProof ProofFrom(string token)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var issuedAt = long.Parse(jwt.Claims.Single(claim => claim.Type == "iat").Value);
        return new ExternalIdentityProof(
            new ExternalIdentityKey(JwtTestAuthority.Issuer, "external-subject"),
            Guid.Parse(jwt.Claims.Single(claim => claim.Type == "session_id").Value),
            DateTimeOffset.FromUnixTimeSeconds(issuedAt - 20),
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(jwt.Claims.Single(claim => claim.Type == "exp").Value)));
    }

    private static async Task SeedSessionAsync(
        FoundationApiFactory factory,
        ExternalIdentityProof proof,
        bool revoke)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var session = new UserSession(
            Guid.NewGuid(),
            factory.ActorUserId,
            proof.Key.Issuer,
            proof.ProviderSessionId,
            proof.ProviderAuthenticatedAt,
            proof.TokenExpiresAt);
        if (revoke)
        {
            session.Revoke(factory.ActorUserId, proof.ProviderAuthenticatedAt.AddMinutes(1), "test_revocation");
        }

        administrator.UserSessions.Add(session);
        await administrator.SaveChangesAsync();
    }

    private static async Task SetBarrierAsync(
        FoundationApiFactory factory,
        DateTimeOffset barrier)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE foundation.users SET sessions_valid_after = @barrier WHERE id = @user_id",
            administrator);
        command.Parameters.AddWithValue("barrier", barrier);
        command.Parameters.AddWithValue("user_id", factory.ActorUserId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RevokeSessionAdmissionExecuteAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION foundation.lock_user_for_session_admission(uuid) FROM vinx_runtime;",
            administrator);
        await command.ExecuteNonQueryAsync();
    }
}
