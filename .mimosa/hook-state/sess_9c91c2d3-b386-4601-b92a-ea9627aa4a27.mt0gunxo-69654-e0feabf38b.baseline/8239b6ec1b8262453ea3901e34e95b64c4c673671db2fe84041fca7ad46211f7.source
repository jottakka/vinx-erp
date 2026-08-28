using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PlatformOrganizationEndpointTests
{
    [Fact]
    public async Task Organization_creation_requires_authentication()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(null, null);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Organization_creation_requires_aal2_even_for_the_exact_global_operator()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal1"
        );
        await SeedPlatformOperatorAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"mfa_assurance_required\"}",
            await response.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task Organization_creation_rejects_an_authenticated_nonoperator_with_aal2()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"platform_operator_required\"}",
            await response.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task Organization_creation_reports_operator_requirement_before_mfa_for_a_nonoperator_with_aal1()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal1"
        );

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"platform_operator_required\"}",
            await response.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task Organization_creation_accepts_only_server_derived_identity_and_company_scope()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync(
                "/foundation/platform/organizations",
                new
                {
                    requestId = Guid.NewGuid(),
                    groupName = "Northwind Group",
                    companyName = "Northwind HQ",
                    ownerDisplayName = "Owner",
                    ownerEmail = "owner@example.test",
                    companyId = Guid.NewGuid(),
                    actorUserId = Guid.NewGuid(),
                    externalIdentityId = Guid.NewGuid(),
                }
            );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"code\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Organization_creation_returns_only_opaque_ids_and_pending_invitation_status()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var expectedProperties = new[]
        {
            "groupId",
            "companyId",
            "ownerUserId",
            "ownerMembershipId",
            "ownerInvitationId",
            "invitationStatus",
        };
        Assert.Equal(
            expectedProperties.Order(),
            root.EnumerateObject().Select(property => property.Name).Order()
        );
        foreach (var propertyName in expectedProperties.Where(name => name != "invitationStatus"))
        {
            Assert.True(Guid.TryParse(root.GetProperty(propertyName).GetString(), out _));
        }

        Assert.Equal("pending_delivery", root.GetProperty("invitationStatus").GetString());
        var body = root.GetRawText();
        Assert.DoesNotContain("owner@example.test", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("issuer", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subject", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Organization_creation_replays_same_body_with_same_ids_without_duplicate_success_audit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        var request = NewRequest();
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            request
        );
        using var replay = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            request
        );

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        foreach (
            var propertyName in new[]
            {
                "groupId",
                "companyId",
                "ownerUserId",
                "ownerMembershipId",
                "ownerInvitationId",
            }
        )
        {
            Assert.Equal(
                firstDocument.RootElement.GetProperty(propertyName).GetString(),
                replayDocument.RootElement.GetProperty(propertyName).GetString()
            );
        }

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM audit.audit_events WHERE action = 'platform.organization.provision' AND outcome = 'success'",
            administrator
        );
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Organization_creation_grants_employee_create_to_the_initial_owner_role_once_even_on_replay()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        var request = NewRequest();
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            request
        );
        using var replay = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            request
        );

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var document = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var companyId = Guid.Parse(document.RootElement.GetProperty("companyId").GetString()!);
        var ownerMembershipId = Guid.Parse(
            document.RootElement.GetProperty("ownerMembershipId").GetString()!
        );

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM foundation.role_capabilities AS grant_record
            INNER JOIN foundation.capabilities AS capability
                ON capability.id = grant_record.capability_id
            INNER JOIN foundation.role_assignments AS assignment
                ON assignment.role_id = grant_record.role_id
                AND assignment.company_id = grant_record.company_id
            WHERE grant_record.company_id = @company_id
              AND assignment.membership_id = @owner_membership_id
              AND capability.name = 'foundation.employees.create'
            """,
            administrator
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("owner_membership_id", ownerMembershipId);

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Organization_replay_does_not_restore_a_soft_deleted_employee_capability()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        var request = NewRequest();
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            request
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var companyId = Guid.Parse(document.RootElement.GetProperty("companyId").GetString()!);
        await using (var administrator = await factory.Fixture.OpenAdministratorConnectionAsync())
        await using (
            var delete = new NpgsqlCommand(
                "UPDATE foundation.role_capabilities SET deleted_at = clock_timestamp() WHERE company_id = @company_id AND capability_id = (SELECT id FROM foundation.capabilities WHERE name = 'foundation.employees.create')",
                administrator
            )
        )
        {
            delete.Parameters.AddWithValue("company_id", companyId);
            await delete.ExecuteNonQueryAsync();
        }
        using var replay = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            request
        );
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using var verify = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM foundation.role_capabilities WHERE company_id = @company_id AND capability_id = (SELECT id FROM foundation.capabilities WHERE name = 'foundation.employees.create') AND deleted_at IS NULL",
            verify
        );
        count.Parameters.AddWithValue("company_id", companyId);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Organization_creation_hashes_canonical_payload_without_delimiter_collisions()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        var requestId = Guid.NewGuid();
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            new
            {
                requestId,
                groupName = "A|B",
                companyName = "C",
                ownerDisplayName = "Owner",
                ownerEmail = "owner@example.test",
            }
        );
        using var collision = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            new
            {
                requestId,
                groupName = "A",
                companyName = "B|C",
                ownerDisplayName = "Owner",
                ownerEmail = "owner@example.test",
            }
        );

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        Assert.Equal(
            "{\"code\":\"idempotency_conflict\"}",
            await collision.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task Organization_creation_rejects_missing_required_field_with_stable_invalid_request()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync(
                "/foundation/platform/organizations",
                new
                {
                    requestId = Guid.NewGuid(),
                    groupName = "Northwind Group",
                    companyName = "Northwind HQ",
                    ownerDisplayName = "Owner",
                }
            );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"code\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Organization_creation_rejects_malformed_json_with_stable_invalid_request()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        using var content = new StringContent(
            "{\"requestId\":",
            System.Text.Encoding.UTF8,
            "application/json"
        );

        using var response = await factory
            .CreateClient()
            .PostAsync("/foundation/platform/organizations", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"code\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
    }

    private static object NewRequest() =>
        new
        {
            requestId = Guid.NewGuid(),
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
}
