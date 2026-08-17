using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Platform;
using Wolverine.Runtime.Serialization;
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
        AssertOpaqueInvitationEnvelope(firstMessage, invitationId, requestId, ownerDisplayName);

        using var replay = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            NewRequest(requestId, ownerDisplayName: ownerDisplayName)
        );
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var replayMessages = await ReadOwnerInvitationMessagesAsync(factory);
        var replayMessage = Assert.Single(replayMessages);
        Assert.Equal(firstMessage.Body, replayMessage.Body);
        Assert.Equal(firstMessage.Metadata, replayMessage.Metadata);
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_request_id_produce_one_creation_and_replays()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        var requestId = Guid.NewGuid();
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(_ =>
                    client.PostAsJsonAsync(
                        "/foundation/platform/organizations",
                        NewRequest(requestId)
                    )
                )
        );

        var createdCount = responses.Count(
            result => result.StatusCode == HttpStatusCode.Created
        );
        Assert.True(createdCount is 0 or 1, $"Expected at most one creation, saw statuses: {string.Join(",", responses.Select(result => (int)result.StatusCode).Order())}");
        Assert.All(
            responses.Where(result => result.StatusCode != HttpStatusCode.Created),
            result =>
                Assert.Contains(
                    (int)result.StatusCode,
                    new[] { 200, 409, 503 }
                )
        );
        Assert.Equal(createdCount, await CountDurableInvitationMessagesAsync(factory));
        Array.ForEach(responses, result => result.Dispose());

        using var replay = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            NewRequest(requestId)
        );
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1L, await CountDurableInvitationMessagesAsync(factory));
    }

    [Fact]
    public async Task Runtime_startup_does_not_create_wolverine_owner_invitation_storage()
    {
        await using var factory = await FoundationApiFactory.CreateWithoutWolverineStorageAsync(
            aal: "aal2"
        );
        var fixture = factory.Fixture;
        var queueBeforeStartup = await OwnerInvitationQueueExistsAsync(fixture);

        Assert.Throws<PostgresException>(() => factory.CreateClient());
        var queueAfterStartup = await OwnerInvitationQueueExistsAsync(fixture);

        Assert.Equal(queueBeforeStartup, queueAfterStartup);
        Assert.False(queueBeforeStartup);
        Assert.False(queueAfterStartup);
    }

    [Fact]
    public async Task Envelope_persistence_failure_before_commit_rolls_back_the_organization_graph()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        await RevokeOutboxInsertAsync(factory);
        var userCountBeforeRequest = await CountUsersAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertOrganizationGraphIsEmptyAsync(factory, userCountBeforeRequest);
        Assert.Empty(await ReadOwnerInvitationMessagesAsync(factory));
    }

    [Fact]
    public async Task Flush_failure_after_commit_keeps_created_response_and_durable_envelope()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        await RevokeQueueInsertAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var createdDocument = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync()
        );
        Assert.Equal(
            "pending_delivery",
            createdDocument.RootElement.GetProperty("invitationStatus").GetString()
        );
        Assert.Equal(1L, await CountOutgoingEnvelopesAsync(factory));
    }

    [Fact]
    public async Task Commit_acknowledgement_failure_does_not_flush_and_retry_does_not_duplicate_the_envelope()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(factory);
        using var client = factory.CreateClient();
        using var admission = await client.GetAsync("/foundation/platform/bootstrap");
        Assert.Equal(HttpStatusCode.OK, admission.StatusCode);
        var requestId = Guid.NewGuid();

        CommitAcknowledgementFailure.Arm(factory.Fixture);
        using var uncertain = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            NewRequest(requestId)
        );

        Assert.Equal(HttpStatusCode.ServiceUnavailable, uncertain.StatusCode);
        Assert.Equal(1L, await CountOutgoingEnvelopesAsync(factory));

        using var replay = await client.PostAsJsonAsync(
            "/foundation/platform/organizations",
            NewRequest(requestId)
        );
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1L, await CountOutgoingEnvelopesAsync(factory));
    }

    [Fact]
    public async Task Runtime_role_cannot_create_or_alter_wolverine_storage()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await using var runtime = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString);
        await runtime.OpenAsync();

        await using var create = new NpgsqlCommand(
            "CREATE TABLE vinx_wolverine.runtime_must_not_create (id integer)",
            runtime
        );
        var createFailure = await Assert.ThrowsAsync<PostgresException>(() =>
            create.ExecuteNonQueryAsync()
        );
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, createFailure.SqlState);

        await using var alter = new NpgsqlCommand(
            "ALTER TABLE vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery ADD COLUMN runtime_must_not_alter integer",
            runtime
        );
        var alterFailure = await Assert.ThrowsAsync<PostgresException>(() =>
            alter.ExecuteNonQueryAsync()
        );
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, alterFailure.SqlState);
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
            "SELECT body, row_to_json(queue)::text FROM vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery AS queue",
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        var messages = new List<OwnerInvitationMessage>();
        while (await reader.ReadAsync())
        {
            var body = reader.GetFieldValue<byte[]>(0);
            var metadata = reader.GetString(1);
            messages.Add(new OwnerInvitationMessage(body, metadata));
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

    private static void AssertOpaqueInvitationEnvelope(
        OwnerInvitationMessage message,
        Guid invitationId,
        Guid requestId,
        string ownerDisplayName
    )
    {
        var envelope = EnvelopeSerializer.Deserialize(message.Body);
        Assert.Equal("application/json", envelope.ContentType);
        using var payload = JsonDocument.Parse(envelope.Data);
        var properties = payload.RootElement.EnumerateObject().ToArray();
        var invitationIdProperty = Assert.Single(properties);
        Assert.Equal("invitationId", invitationIdProperty.Name);
        Assert.Equal(JsonValueKind.String, invitationIdProperty.Value.ValueKind);
        Assert.Equal(invitationId, invitationIdProperty.Value.GetGuid());
        var requested = Assert.IsType<OwnerInvitationDeliveryRequested>(
            new SystemTextJsonSerializer(
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            ).ReadFromData(typeof(OwnerInvitationDeliveryRequested), envelope)
        );
        Assert.Equal(invitationId, requested.InvitationId);
        Assert.Contains(
            "OwnerInvitationDeliveryRequested",
            message.Metadata,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            invitationId.ToString("D"),
            message.Metadata,
            StringComparison.OrdinalIgnoreCase
        );
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
            Assert.DoesNotContain(forbidden, message.Metadata, StringComparison.OrdinalIgnoreCase);
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

    private static async Task RevokeOutboxInsertAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE INSERT ON vinx_wolverine.wolverine_outgoing_envelopes FROM vinx_runtime;",
            administrator
        );
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RevokeQueueInsertAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE INSERT ON vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery FROM vinx_runtime;",
            administrator
        );
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountDurableInvitationMessagesAsync(
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

    private static async Task<long> CountOutgoingEnvelopesAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM vinx_wolverine.wolverine_outgoing_envelopes",
            administrator
        );
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertOrganizationGraphIsEmptyAsync(
        FoundationApiFactory factory,
        long expectedUserCount
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM foundation.groups),
                (SELECT count(*) FROM foundation.companies),
                (SELECT count(*) FROM foundation.users),
                (SELECT count(*) FROM foundation.memberships),
                (SELECT count(*) FROM foundation.role_assignments),
                (SELECT count(*) FROM foundation.pending_owner_invitations),
                (SELECT count(*) FROM foundation.organization_provisioning_receipts),
                (SELECT count(*) FROM audit.audit_events WHERE action = 'platform.organization.provision' AND outcome = 'success')
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expectedUserCount, reader.GetInt64(2));
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (index == 2)
            {
                continue;
            }

            Assert.Equal(0L, reader.GetInt64(index));
        }
    }

    private static async Task<long> CountUsersAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM foundation.users",
            administrator
        );
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record OwnerInvitationMessage(byte[] Body, string Metadata);
}
