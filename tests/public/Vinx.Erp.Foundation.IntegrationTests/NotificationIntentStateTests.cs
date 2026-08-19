using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class NotificationIntentStateTests
{
    [Fact]
    public async Task Envelope_persistence_failure_before_commit_rolls_back_intents()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        await RevokeOutboxInsertAsync(factory);

        var result = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                WhatsAppTemplateKey: seed.WhatsAppTemplateKey
            )
        );

        Assert.Equal(NotificationIntentEnqueueState.Unavailable, result.State);
        Assert.Empty(await NotificationIntentSeed.ReadIntentsAsync(factory));
        Assert.Empty(await NotificationIntentSeed.ReadNotificationMessagesAsync(factory));
        Assert.Equal(
            0,
            await NotificationIntentSeed.CountAuditEventsAsync(
                factory,
                "notification.intent.enqueue"
            )
        );
    }

    [Fact]
    public async Task Replayed_event_does_not_duplicate_deliveries_or_touch_status()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        var command = new NotificationIntentCommand(
            seed.CompanyId,
            seed.RecipientUserId,
            NotificationIntentSeed.NewEventKey(),
            seed.InAppTemplateKey,
            WhatsAppTemplateKey: seed.WhatsAppTemplateKey
        );

        var first = await NotificationIntentSeed.EnqueueAsync(factory, command);
        Assert.Equal(NotificationIntentEnqueueState.Enqueued, first.State);
        var rowsAfterFirst = await NotificationIntentSeed.ReadIntentsAsync(factory);

        var replay = await NotificationIntentSeed.EnqueueAsync(factory, command);

        Assert.Equal(NotificationIntentEnqueueState.Enqueued, replay.State);
        Assert.False(replay.WhatsApp!.Created);
        Assert.Equal(first.WhatsApp!.DeliveryId, replay.WhatsApp.DeliveryId);
        Assert.False(replay.InApp!.Created);
        Assert.Equal(first.InApp!.DeliveryId, replay.InApp.DeliveryId);

        var rowsAfterReplay = await NotificationIntentSeed.ReadIntentsAsync(factory);
        Assert.Equal(rowsAfterFirst.Count, rowsAfterReplay.Count);
        Assert.Equal(
            rowsAfterFirst.Select(row => (row.Id, row.Status, row.AttemptCount, row.UpdatedAt)),
            rowsAfterReplay.Select(row => (row.Id, row.Status, row.AttemptCount, row.UpdatedAt))
        );

        var messages = await NotificationIntentSeed.ReadNotificationMessagesAsync(factory);
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task Opt_out_after_first_enqueue_blocks_new_whatsapp_events_but_preserves_existing()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        var firstEventKey = NotificationIntentSeed.NewEventKey();
        var first = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                firstEventKey,
                seed.InAppTemplateKey,
                WhatsAppTemplateKey: seed.WhatsAppTemplateKey
            )
        );
        Assert.True(first.WhatsApp!.Created);
        var whatsappRowAfterFirst = Assert.Single(
            await NotificationIntentSeed.ReadIntentsAsync(factory),
            row => row.Channel == "whatsapp"
        );

        var optOut = await NotificationIntentSeed.SetPreferenceViaServiceAsync(
            factory,
            seed.RecipientUserId,
            seed.CompanyId,
            optedIn: false
        );
        Assert.Equal(NotificationPreferenceState.Updated, optOut.State);

        var second = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                WhatsAppTemplateKey: seed.WhatsAppTemplateKey
            )
        );

        Assert.Equal(NotificationIntentEnqueueState.Enqueued, second.State);
        Assert.False(second.WhatsApp!.Created);
        Assert.Equal("suppressed_channel_opt_out", second.WhatsApp.Status);
        Assert.True(second.InApp!.Created);

        var rows = await NotificationIntentSeed.ReadIntentsAsync(factory);
        Assert.Equal(3, rows.Count);
        var preservedWhatsappRow = Assert.Single(
            rows,
            row => row.Channel == "whatsapp"
        );
        Assert.Equal(whatsappRowAfterFirst.Id, preservedWhatsappRow.Id);
        Assert.Equal(
            (whatsappRowAfterFirst.Status, whatsappRowAfterFirst.AttemptCount, whatsappRowAfterFirst.UpdatedAt),
            (preservedWhatsappRow.Status, preservedWhatsappRow.AttemptCount, preservedWhatsappRow.UpdatedAt)
        );

        var messages = await NotificationIntentSeed.ReadNotificationMessagesAsync(factory);
        Assert.Equal(3, messages.Count);
        Assert.Equal(
            1,
            await NotificationIntentSeed.CountAuditEventsAsync(
                factory,
                "notification.preference.change"
            )
        );
    }

    [Fact]
    public async Task Startup_without_provisioned_storage_does_not_create_notification_storage()
    {
        await using var factory = await FoundationApiFactory.CreateWithoutWolverineStorageAsync();
        Assert.False(await NotificationIntentSeed.NotificationQueueExistsAsync(factory));
        Assert.False(await WolverineOutgoingStorageExistsAsync(factory));

        Assert.Throws<PostgresException>(() => factory.CreateClient());

        Assert.False(await NotificationIntentSeed.NotificationQueueExistsAsync(factory));
        Assert.False(await WolverineOutgoingStorageExistsAsync(factory));
    }

    private static async Task<bool> WolverineOutgoingStorageExistsAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var exists = new NpgsqlCommand(
            "SELECT to_regclass('vinx_wolverine.wolverine_outgoing_envelopes')::text",
            administrator
        );
        var tableName = await exists.ExecuteScalarAsync();
        return tableName is string;
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
}
