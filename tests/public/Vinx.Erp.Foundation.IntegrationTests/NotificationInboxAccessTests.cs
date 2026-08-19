using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class NotificationInboxAccessTests
{
    [Fact]
    public async Task Active_member_without_any_capability_still_sees_the_personal_inbox()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var deliveryId = await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        var capabilityGrants = (long)
            await NotificationInboxSeed.ExecuteScalarAsync(
                administrator,
                """
                SELECT count(*) FROM foundation.role_capabilities AS grant_row
                WHERE grant_row.company_id IN (
                    SELECT membership.company_id FROM foundation.memberships AS membership
                    WHERE membership.user_id = @user_id AND membership.deleted_at IS NULL)
                """,
                ("user_id", factory.ActorUserId)
            )!;

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/notifications/inbox?limit=10");

        Assert.Equal(0, capabilityGrants);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains(deliveryId.ToString(), payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_member_recipient_is_invisible_and_uncounted_for_the_current_actor()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var otherCompanyId = Guid.NewGuid();
        var otherRecipientId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await SeedForeignDeliveredIntentAsync(
            administrator,
            factory.ActorUserId,
            otherCompanyId,
            otherRecipientId,
            suffix
        );

        using var client = factory.CreateClient();
        using var listResponse = await client.GetAsync("/notifications/inbox?limit=10");
        using var countResponse = await client.GetAsync("/notifications/inbox/unread-count");

        listResponse.EnsureSuccessStatusCode();
        Assert.Empty(
            (await listResponse.Content.ReadFromJsonAsync<InboxPayload>())!.Items
        );
        countResponse.EnsureSuccessStatusCode();
        var count = await countResponse.Content.ReadFromJsonAsync<CountPayload>();
        Assert.Equal(0, count!.UnreadCount);
    }

    [Fact]
    public async Task Out_of_scope_delivery_returns_the_same_404_as_a_missing_one()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var otherCompanyId = Guid.NewGuid();
        var otherRecipientId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        var foreignDeliveryId = await SeedForeignDeliveredIntentAsync(
            administrator,
            factory.ActorUserId,
            otherCompanyId,
            otherRecipientId,
            suffix
        );
        using var client = factory.CreateClient();

        using var foreign = await client.PostAsync(
            $"/notifications/inbox/{foreignDeliveryId}/read",
            content: null
        );
        using var missing = await client.PostAsync(
            $"/notifications/inbox/{Guid.NewGuid()}/read",
            content: null
        );

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(
            await foreign.Content.ReadAsStringAsync(),
            await missing.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task Revoked_membership_removes_entries_from_list_and_count()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var alfaCompanyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var betaCompanyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, alfaCompanyId);
        await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, betaCompanyId);

        using var client = factory.CreateClient();
        using var beforeList = await client.GetAsync("/notifications/inbox?limit=10");
        using var beforeCount = await client.GetAsync("/notifications/inbox/unread-count");
        var before = (await beforeList.Content.ReadFromJsonAsync<InboxPayload>())!;

        await NotificationInboxSeed.RevokeMembershipAsync(factory, alfaCompanyId, factory.ActorUserId);

        using var afterList = await client.GetAsync("/notifications/inbox?limit=10");
        using var afterCountResponse = await client.GetAsync(
            "/notifications/inbox/unread-count"
        );
        var after = (await afterList.Content.ReadFromJsonAsync<InboxPayload>())!;
        var afterCount = (await afterCountResponse.Content.ReadFromJsonAsync<CountPayload>())!;

        Assert.Equal(2, before.Items.Count);
        Assert.Single(after.Items);
        Assert.Equal(betaCompanyId, after.Items.Single().CompanyId);
        Assert.Equal(1, afterCount.UnreadCount);
    }

    [Fact]
    public async Task Soft_deleted_company_removes_entries_from_list()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);

        await NotificationInboxSeed.SoftDeleteCompanyAsync(factory, companyId);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/notifications/inbox?limit=10");
        var payload = (await response.Content.ReadFromJsonAsync<InboxPayload>())!;
        Assert.Empty(payload.Items);
    }

    [Fact]
    public async Task Runtime_keeps_no_direct_table_access_and_functions_run_only_for_runtime()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                has_table_privilege('vinx_runtime', 'notifications.notification_intents', 'SELECT') AS runtime_select,
                has_table_privilege('vinx_runtime', 'notifications.notification_intents', 'UPDATE') AS runtime_update,
                has_function_privilege('vinx_runtime', 'notifications.list_inbox_notifications(integer, timestamp with time zone, uuid)', 'EXECUTE') AS runtime_list,
                has_function_privilege('vinx_runtime', 'notifications.count_unread_inbox_notifications()', 'EXECUTE') AS runtime_count,
                has_function_privilege('vinx_runtime', 'notifications.mark_inbox_notification_read(uuid)', 'EXECUTE') AS runtime_mark,
                has_function_privilege('vinx_worker', 'notifications.list_inbox_notifications(integer, timestamp with time zone, uuid)', 'EXECUTE') AS worker_list,
                has_function_privilege('vinx_worker', 'notifications.count_unread_inbox_notifications()', 'EXECUTE') AS worker_count,
                has_function_privilege('vinx_worker', 'notifications.mark_inbox_notification_read(uuid)', 'EXECUTE') AS worker_mark,
                has_function_privilege('vinx_notifications_owner', 'foundation.current_actor_memberships()', 'EXECUTE') AS owner_memberships
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
        Assert.True(reader.GetBoolean(8));
    }

    /// <summary>
    /// Cria empresa/destinatário estranhos ao ator e um intent in_app entregue,
    /// retornando o id da entrega (fora do escopo do ator autenticado).
    /// </summary>
    private static async Task<Guid> SeedForeignDeliveredIntentAsync(
        NpgsqlConnection administrator,
        Guid actorUserId,
        Guid companyId,
        Guid recipientId,
        string suffix
    )
    {
        var groupId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await ExecuteAsync(
            administrator,
            """
            INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@group_id, @name, @actor, @actor, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@company_id, @group_id, @name, @actor, @actor, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES (@recipient_id, @display, @email, clock_timestamp());
            INSERT INTO foundation.memberships (id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (gen_random_uuid(), @company_id, @recipient_id, @actor, @actor, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO notifications.notification_templates (company_id, channel, template_key, is_approved)
            VALUES (@company_id, 'in_app', 'alert.in_app', true);
            INSERT INTO notifications.notification_intents (
                id, company_id, recipient_user_id, event_key, channel, template_key,
                status, attempt_count, correlation_id, created_by_user_id,
                provider_attempt_id, provider_claimed_at, provider_resolved_at)
            VALUES (
                @delivery_id, @company_id, @recipient_id, @event_key, 'in_app', 'alert.in_app',
                'delivered', 1, @correlation_id, @actor,
                gen_random_uuid(), clock_timestamp(), clock_timestamp());
            """,
            ("group_id", groupId),
            ("company_id", companyId),
            ("name", $"foreign-company-{suffix}"),
            ("actor", actorUserId),
            ("request_id", Guid.NewGuid()),
            ("recipient_id", recipientId),
            ("display", $"foreign-recipient-{suffix}"),
            ("email", $"foreign-{suffix}@example.test"),
            ("delivery_id", deliveryId),
            ("event_key", $"event-{suffix}"),
            ("correlation_id", correlationId)
        );
        return deliveryId;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection administrator,
        string commandText,
        params (string Name, object Value)[] parameters
    )
    {
        await using var command = new NpgsqlCommand(commandText, administrator);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private sealed record InboxPayload(IReadOnlyList<InboxItem> Items, string? NextCursor);

    private sealed record InboxItem(Guid Id, Guid CompanyId);

    private sealed record CountPayload(long UnreadCount);
}
