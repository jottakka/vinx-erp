using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PlatformOrganizationProvisioningFailureTests
{
    [Fact]
    public async Task Audit_append_failure_returns_safe_unavailable_and_rolls_back_the_entire_organization_graph()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(aal: "aal2");
        await SeedPlatformOperatorAsync(factory);
        await RevokeProvisioningAuditPrivilegeAsync(factory);

        using var response = await factory.CreateClient().PostAsJsonAsync(
            "/foundation/platform/organizations",
            new
            {
                requestId = Guid.NewGuid(),
                groupName = "Northwind Group",
                companyName = "Northwind HQ",
                ownerDisplayName = "Owner",
                ownerEmail = "owner@example.test"
            });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"platform_organization_unavailable\"}",
            await response.Content.ReadAsStringAsync());

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM foundation.groups),
                (SELECT count(*) FROM foundation.companies),
                (SELECT count(*) FROM foundation.memberships),
                (SELECT count(*) FROM foundation.role_assignments),
                (SELECT count(*) FROM foundation.pending_owner_invitations),
                (SELECT count(*) FROM foundation.organization_provisioning_receipts),
                (SELECT count(*) FROM audit.audit_events WHERE action = 'platform.organization.provision' AND outcome = 'success')
            """,
            administrator);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < reader.FieldCount; index++)
        {
            Assert.Equal(0L, reader.GetInt64(index));
        }
    }

    [Fact]
    public async Task Missing_required_owner_capability_returns_safe_unavailable_and_rolls_back_the_entire_organization_graph()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(aal: "aal2");
        await SeedPlatformOperatorAsync(factory);
        await DeleteRequiredCapabilityAsync(factory);

        using var response = await factory.CreateClient().PostAsJsonAsync(
            "/foundation/platform/organizations",
            new
            {
                requestId = Guid.NewGuid(),
                groupName = "Northwind Group",
                companyName = "Northwind HQ",
                ownerDisplayName = "Owner",
                ownerEmail = "owner@example.test"
            });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"platform_organization_unavailable\"}",
            await response.Content.ReadAsStringAsync());

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM foundation.groups),
                (SELECT count(*) FROM foundation.companies),
                (SELECT count(*) FROM foundation.memberships),
                (SELECT count(*) FROM foundation.role_assignments),
                (SELECT count(*) FROM foundation.pending_owner_invitations),
                (SELECT count(*) FROM foundation.organization_provisioning_receipts),
                (SELECT count(*) FROM audit.audit_events WHERE action = 'platform.organization.provision' AND outcome = 'success')
            """,
            administrator);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < reader.FieldCount; index++)
        {
            Assert.Equal(0L, reader.GetInt64(index));
        }
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
            administrator);
        command.Parameters.AddWithValue("user_id", factory.ActorUserId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RevokeProvisioningAuditPrivilegeAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb) FROM vinx_provisioning_owner;",
            administrator);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteRequiredCapabilityAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM foundation.capabilities WHERE name = 'foundation.files.confirm';",
            administrator);
        await command.ExecuteNonQueryAsync();
    }
}
