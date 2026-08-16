using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PlatformOrganizationProvisioningOutboxTests
{
    [Fact]
    public async Task Organization_creation_persists_one_opaque_owner_invitation_message_and_replay_does_not_duplicate_it()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2");
        await SeedPlatformOperatorAsync(factory);
        var requestId = Guid.NewGuid();
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            NewRequest(requestId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var invitationId = Guid.Parse(
            createdDocument.RootElement.GetProperty("ownerInvitationId").GetString()!);

        var firstMessages = await ReadOwnerInvitationMessagesAsync(factory);
        var firstMessage = Assert.Single(firstMessages);
        Assert.Contains(invitationId.ToString("D"), firstMessage.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Northwind Group", firstMessage.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Northwind HQ", firstMessage.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("owner@example.test", firstMessage.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", firstMessage.Body, StringComparison.OrdinalIgnoreCase);

        using var replay = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            NewRequest(requestId));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var replayMessages = await ReadOwnerInvitationMessagesAsync(factory);
        Assert.Single(replayMessages);
        Assert.Equal(firstMessage, replayMessages[0]);
    }

    [Fact]
    public async Task Organization_creation_rejects_aal1_without_writing_an_owner_invitation_message()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal1");
        await SeedPlatformOperatorAsync(factory);

        using var response = await factory.CreateClient().PostAsJsonAsync(
            "/foundation/platform/organizations",
            NewRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"mfa_assurance_required\"}",
            await response.Content.ReadAsStringAsync());
        Assert.Empty(await ReadOwnerInvitationMessagesAsync(factory));
    }

    [Fact]
    public async Task Organization_creation_rejects_a_payload_conflict_without_writing_an_extra_owner_invitation_message()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2");
        await SeedPlatformOperatorAsync(factory);
        var requestId = Guid.NewGuid();
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            NewRequest(requestId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var conflict = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            NewRequest(requestId, "Contoso Group", "Contoso HQ"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "{\"code\":\"idempotency_conflict\"}",
            await conflict.Content.ReadAsStringAsync());
        Assert.Single(await ReadOwnerInvitationMessagesAsync(factory));
    }

    private static object NewRequest(
        Guid requestId,
        string groupName = "Northwind Group",
        string companyName = "Northwind HQ") => new
    {
        requestId,
        groupName,
        companyName,
        ownerDisplayName = "Owner",
        ownerEmail = "owner@example.test",
    };

    private static async Task<IReadOnlyList<OwnerInvitationMessage>> ReadOwnerInvitationMessagesAsync(
        FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var exists = new NpgsqlCommand(
            "SELECT to_regclass('vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery')::text",
            administrator);
        var tableName = await exists.ExecuteScalarAsync();
        Assert.True(
            tableName is string,
            "Wolverine owner-invitation-delivery storage is absent; startup/runtime must not create it.");

        await using var command = new NpgsqlCommand(
            "SELECT body, headers FROM vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery",
            administrator);
        await using var reader = await command.ExecuteReaderAsync();
        var messages = new List<OwnerInvitationMessage>();
        while (await reader.ReadAsync())
        {
            var body = Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(0));
            var headers = reader.IsDBNull(1) ? string.Empty : reader.GetFieldValue<string>(1);
            messages.Add(new OwnerInvitationMessage(body, headers));
        }

        return messages;
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

    private sealed record OwnerInvitationMessage(string Body, string Headers);
}
