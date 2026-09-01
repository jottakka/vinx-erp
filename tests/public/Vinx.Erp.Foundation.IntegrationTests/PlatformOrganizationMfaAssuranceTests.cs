using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// A policy platform.operator.mfa_assurance rederiva o AAL2 no uso do privilégio:
/// um cookie aal2 só vale enquanto a Admin API do Supabase confirma um fator
/// verificado corrente; indisponibilidade do provedor fecha o gate e cookie aal1
/// nega sem chamar o provedor.
/// </summary>
public sealed class PlatformOrganizationMfaAssuranceTests
{
    [Fact]
    public async Task Organization_creation_succeeds_for_an_aal2_operator_with_a_currently_verified_factor()
    {
        var verifiedFactorHandler = new StubSupabaseAdminFactorHandler();
        await using var factory = await FoundationApiFactory
            .CreateWithAuthenticatedIdentityAndMfaProviderAsync(verifiedFactorHandler, aal: "aal2");
        await SeedPlatformOperatorAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(
            verifiedFactorHandler.CallCount >= 1,
            "The provisioning request must re-derive MFA assurance against the admin endpoint.");
    }

    [Fact]
    public async Task Organization_creation_rejects_an_aal2_operator_whose_verified_factor_is_gone()
    {
        await using var factory = await FoundationApiFactory
            .CreateWithAuthenticatedIdentityAndMfaProviderAsync(
                new StubSupabaseAdminFactorHandler(WithoutVerifiedFactorsHandler),
                aal: "aal2");
        await SeedPlatformOperatorAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"mfa_assurance_required\"}",
            await response.Content.ReadAsStringAsync()
        );
        Assert.Equal(0L, await CountDurableOwnerInvitationMessagesAsync(factory));
    }

    [Fact]
    public async Task Organization_creation_fails_closed_when_the_admin_endpoint_is_unreachable()
    {
        await using var factory = await FoundationApiFactory
            .CreateWithAuthenticatedIdentityAndMfaProviderAsync(
                new StubSupabaseAdminFactorHandler(_ =>
                    throw new HttpRequestException("connection refused")),
                aal: "aal2");
        await SeedPlatformOperatorAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"mfa_assurance_required\"}",
            await response.Content.ReadAsStringAsync()
        );
        Assert.Equal(0L, await CountDurableOwnerInvitationMessagesAsync(factory));
    }

    [Fact]
    public async Task Organization_creation_rejects_an_aal1_operator_without_calling_the_admin_endpoint()
    {
        var countingHandler = new StubSupabaseAdminFactorHandler(_ =>
            throw new HttpRequestException("The provider must not be called for aal1."));
        await using var factory = await FoundationApiFactory
            .CreateWithAuthenticatedIdentityAndMfaProviderAsync(countingHandler, aal: "aal1");
        await SeedPlatformOperatorAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"mfa_assurance_required\"}",
            await response.Content.ReadAsStringAsync()
        );
        Assert.Equal(0, countingHandler.CallCount);
    }

    private static readonly Func<HttpRequestMessage, HttpResponseMessage> WithoutVerifiedFactorsHandler =
        request =>
        {
            var subject = Uri.UnescapeDataString(
                request.RequestUri!.AbsolutePath.Split('/').Last());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        id = subject,
                        factors = Array.Empty<object>(),
                    }),
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        };

    private static object NewRequest(Guid requestId) => new
    {
        requestId,
        groupName = "Northwind Group",
        companyName = "Northwind HQ",
        ownerDisplayName = "Owner",
        ownerEmail = "owner@example.test",
    };

    private static async Task SeedPlatformOperatorAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.platform_operators (singleton, external_identity_id)
            SELECT 1, identity_record.id
            FROM foundation.external_identities AS identity_record
            WHERE identity_record.user_id = @user_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("user_id", factory.ActorUserId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountDurableOwnerInvitationMessagesAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM vinx_wolverine.wolverine_outgoing_envelopes) "
                + "+ (SELECT count(*) FROM vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery)",
            administrator
        );
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
