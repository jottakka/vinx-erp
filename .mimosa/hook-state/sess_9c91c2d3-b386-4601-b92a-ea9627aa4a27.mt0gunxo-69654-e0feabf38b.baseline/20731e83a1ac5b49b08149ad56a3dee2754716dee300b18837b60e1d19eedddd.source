using System.Net;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PlatformOperatorEndpointTests
{
    [Fact]
    public async Task Platform_bootstrap_rejects_an_authenticated_nonoperator_with_only_the_stable_error()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();

        using var response = await factory
            .CreateClient()
            .GetAsync("/foundation/platform/bootstrap");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"platform_operator_required\"}",
            await response.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task Platform_bootstrap_returns_only_the_operator_projection_for_a_seeded_operator()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedPlatformOperatorAsync(factory);

        using var response = await factory
            .CreateClient()
            .GetAsync("/foundation/platform/bootstrap");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"isPlatformOperator\":true}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Platform_bootstrap_rejects_a_second_external_identity_linked_to_the_pinned_operator_user()
    {
        const string issuer = "https://project.supabase.co/auth/v1";
        const string alternateSubject = "operator-alternate-subject";
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(
            issuer,
            alternateSubject
        );
        await SeedPinnedOperatorAndAlternateIdentityAsync(factory, issuer, alternateSubject);

        using var response = await factory
            .CreateClient()
            .GetAsync("/foundation/platform/bootstrap");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"platform_operator_required\"}",
            await response.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task Platform_bootstrap_and_bootstrap_fail_closed_when_operator_function_execution_is_revoked()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await RevokePlatformOperatorFunctionExecuteAsync(factory);

        using var platformResponse = await factory
            .CreateClient()
            .GetAsync("/foundation/platform/bootstrap");
        using var bootstrapResponse = await factory
            .CreateClient()
            .GetAsync("/foundation/bootstrap");

        await AssertOnlyPlatformOperatorUnavailableAsync(platformResponse);
        await AssertOnlyPlatformOperatorUnavailableAsync(bootstrapResponse);
    }

    [Fact]
    public async Task Bootstrap_includes_a_false_platform_projection_without_identity_metadata_for_a_nonoperator()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();

        using var response = await factory.CreateClient().GetAsync("/foundation/bootstrap");
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains(
            "\"platform\":{\"isPlatformOperator\":false}",
            body,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("issuer", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subject", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bootstrap_includes_a_true_platform_projection_without_identity_metadata_for_an_operator()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedPlatformOperatorAsync(factory);

        using var response = await factory.CreateClient().GetAsync("/foundation/bootstrap");
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains(
            "\"platform\":{\"isPlatformOperator\":true}",
            body,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("issuer", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subject", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);
    }

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

    private static async Task SeedPinnedOperatorAndAlternateIdentityAsync(
        FoundationApiFactory factory,
        string issuer,
        string alternateSubject
    )
    {
        var pinnedIdentityId = Guid.NewGuid();
        var alternateIdentityId = Guid.NewGuid();
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES (@user_id, 'Pinned operator', 'pinned-operator@example.test', clock_timestamp());

            INSERT INTO foundation.external_identities (id, user_id, issuer, subject, created_at)
            VALUES
                (@pinned_identity_id, @user_id, @issuer, 'operator-pinned-subject', clock_timestamp()),
                (@alternate_identity_id, @user_id, @issuer, @alternate_subject, clock_timestamp());

            INSERT INTO foundation.platform_operators (singleton, external_identity_id)
            VALUES (1, @pinned_identity_id);
            """,
            administrator
        );
        command.Parameters.AddWithValue("user_id", factory.ActorUserId);
        command.Parameters.AddWithValue("pinned_identity_id", pinnedIdentityId);
        command.Parameters.AddWithValue("alternate_identity_id", alternateIdentityId);
        command.Parameters.AddWithValue("issuer", issuer);
        command.Parameters.AddWithValue("alternate_subject", alternateSubject);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RevokePlatformOperatorFunctionExecuteAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION foundation.current_actor_is_platform_operator() FROM vinx_runtime;",
            administrator
        );
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertOnlyPlatformOperatorUnavailableAsync(
        HttpResponseMessage response
    )
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"platform_operator_unavailable\"}",
            await response.Content.ReadAsStringAsync()
        );
    }
}
