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
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        var requestId = Guid.NewGuid();
        var ownerDisplayName = $"outbox-display-name-{Guid.NewGuid():N}";
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            NewRequest(requestId, ownerDisplayName: ownerDisplayName)
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var invitationId = Guid.Parse(
            createdDocument.RootElement.GetProperty("ownerInvitationId").GetString()!
        );

        var firstMessages = await ReadOwnerInvitationMessagesAsync(factory);
        var firstMessage = Assert.Single(firstMessages);
        AssertOpaqueInvitationBody(firstMessage.Body, invitationId);
        AssertOpaqueInvitationHeaders(firstMessage.Headers, requestId, ownerDisplayName);

        using var replay = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            NewRequest(requestId, ownerDisplayName: ownerDisplayName)
        );
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var replayMessages = await ReadOwnerInvitationMessagesAsync(factory);
        Assert.Single(replayMessages);
        Assert.Equal(firstMessage, replayMessages[0]);
    }

    [Fact]
    public async Task Runtime_startup_does_not_create_wolverine_owner_invitation_storage()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var queueBeforeStartup = await OwnerInvitationQueueExistsAsync(fixture);

        await using var factory = await FoundationApiFactory.CreateForLifecycleTestAsync(
            fixture,
            () => ValueTask.CompletedTask,
            () => ValueTask.CompletedTask
        );
        using var client = factory.CreateClient();
        var queueAfterStartup = await OwnerInvitationQueueExistsAsync(fixture);

        Assert.Equal(queueBeforeStartup, queueAfterStartup);
        Assert.False(queueBeforeStartup);
        Assert.False(queueAfterStartup);
    }

    [Fact]
    public async Task Organization_creation_rejects_aal1_without_writing_an_owner_invitation_message()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal1"
        );
        await SeedPlatformOperatorAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"mfa_assurance_required\"}",
            await response.Content.ReadAsStringAsync()
        );
        Assert.Empty(await ReadOwnerInvitationMessagesAsync(factory));
    }

    [Fact]
    public async Task Organization_creation_rejects_an_aal2_nonoperator_without_writing_an_owner_invitation_message()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"platform_operator_required\"}",
            await response.Content.ReadAsStringAsync()
        );
        Assert.Empty(await ReadOwnerInvitationMessagesAsync(factory));
    }

    [Fact]
    public async Task Organization_creation_rejects_a_payload_conflict_without_writing_an_extra_owner_invitation_message()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        var requestId = Guid.NewGuid();
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            NewRequest(requestId)
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var conflict = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            NewRequest(requestId, "Contoso Group", "Contoso HQ")
        );
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "{\"code\":\"idempotency_conflict\"}",
            await conflict.Content.ReadAsStringAsync()
        );
        Assert.Single(await ReadOwnerInvitationMessagesAsync(factory));
    }

    private static object NewRequest(
        Guid requestId,
        string groupName = "Northwind Group",
        string companyName = "Northwind HQ",
        string ownerDisplayName = "Owner",
        string ownerEmail = "owner@example.test"
    ) =>
        new
        {
            requestId,
            groupName,
            companyName,
            ownerDisplayName,
            ownerEmail,
        };

    private static async Task<
        IReadOnlyList<OwnerInvitationMessage>
    > ReadOwnerInvitationMessagesAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        Assert.True(
            await OwnerInvitationQueueExistsAsync(administrator),
            "Wolverine owner-invitation-delivery storage must be provisioned by deploy before message assertions; tests never create it."
        );

        await using var command = new NpgsqlCommand(
            "SELECT body, headers FROM vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery",
            administrator
        );
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

    private static async Task<bool> OwnerInvitationQueueExistsAsync(
        FoundationPostgresFixture fixture
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        return await OwnerInvitationQueueExistsAsync(administrator);
    }

    private static async Task<bool> OwnerInvitationQueueExistsAsync(NpgsqlConnection administrator)
    {
        await using var exists = new NpgsqlCommand(
            "SELECT to_regclass('vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery')::text",
            administrator
        );
        var tableName = await exists.ExecuteScalarAsync();
        return tableName is string;
    }

    private static void AssertOpaqueInvitationBody(string body, Guid invitationId)
    {
        using var document = JsonDocument.Parse(body);
        var properties = document.RootElement.EnumerateObject().ToArray();
        Assert.Single(properties);
        Assert.Equal("invitationId", properties[0].Name, ignoreCase: true);
        Assert.Equal(invitationId, Guid.Parse(properties[0].Value.GetString() ?? string.Empty));
    }

    private static void AssertOpaqueInvitationHeaders(
        string headers,
        Guid requestId,
        string ownerDisplayName
    )
    {
        foreach (
            var forbidden in new[]
            {
                "Northwind Group",
                "Northwind HQ",
                ownerDisplayName,
                "owner@example.test",
                "groupName",
                "companyName",
                "ownerDisplayName",
                "ownerEmail",
                "payload",
                "payloadHash",
                requestId.ToString("D"),
            }
        )
        {
            Assert.DoesNotContain(forbidden, headers, StringComparison.OrdinalIgnoreCase);
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
            administrator
        );
        command.Parameters.AddWithValue("user_id", factory.ActorUserId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record OwnerInvitationMessage(string Body, string Headers);
}
