using System.Text.Json;
using System.Text;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class TelegramNotificationIntentTests
{
    [Fact]
    public async Task Eligible_telegram_event_creates_telegram_and_in_app_intents_with_opaque_messages()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: true,
            telegramContactValidated: true
        );

        var result = await EnqueueTelegramAsync(factory, seed);

        Assert.Equal(NotificationIntentEnqueueState.Enqueued, result.State);
        Assert.Null(result.WhatsApp);
        Assert.True(result.Telegram!.Created);
        Assert.True(result.InApp!.Created);
        var intents = await NotificationIntentSeed.ReadIntentsAsync(factory);
        Assert.Equal(["in_app", "telegram"], [.. intents.Select(intent => intent.Channel).Order()]);
        var messages = await NotificationIntentSeed.ReadNotificationMessagesAsync(factory);
        Assert.Equal(2, messages.Count);
        Assert.All(
            messages,
            message => AssertOpaqueDeliveryEnvelope(
                message,
                [result.Telegram.DeliveryId!.Value, result.InApp.DeliveryId!.Value],
                seed.TelegramText,
                seed.TelegramAddress
            )
        );
    }

    [Fact]
    public async Task Eligible_telegram_and_whatsapp_event_creates_three_independent_outcomes()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: true,
            telegramContactValidated: true
        );
        await NotificationIntentSeed.SeedPreferenceAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            optedIn: true
        );

        var result = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                WhatsAppTemplateKey: NotificationIntentSeed.WhatsAppTemplateKey,
                TelegramTemplateKey: seed.TelegramTemplateKey
            )
        );

        Assert.Equal(NotificationIntentEnqueueState.Enqueued, result.State);
        Assert.True(result.WhatsApp!.Created);
        Assert.True(result.Telegram!.Created);
        Assert.True(result.InApp!.Created);
        Assert.Equal(
            ["in_app", "telegram", "whatsapp"],
            [.. (await NotificationIntentSeed.ReadIntentsAsync(factory)).Select(intent => intent.Channel).Order()]
        );
        Assert.Equal(3, (await NotificationIntentSeed.ReadNotificationMessagesAsync(factory)).Count);
    }

    [Fact]
    public async Task Telegram_opt_out_suppresses_only_telegram_and_keeps_mandatory_in_app()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: false,
            telegramContactValidated: true
        );

        var result = await EnqueueTelegramAsync(factory, seed);

        Assert.Equal(NotificationIntentEnqueueState.Enqueued, result.State);
        Assert.Equal("suppressed_channel_opt_out", result.Telegram!.Status);
        Assert.Equal("channel_opt_out", result.Telegram.SuppressionReason);
        Assert.False(result.Telegram.Created);
        Assert.True(result.InApp!.Created);
        var intents = await NotificationIntentSeed.ReadIntentsAsync(factory);
        Assert.Equal(["in_app"], [.. intents.Select(intent => intent.Channel)]);
        Assert.Single(await NotificationIntentSeed.ReadNotificationMessagesAsync(factory));
    }

    [Fact]
    public async Task Missing_telegram_text_rejects_enqueue_without_telegram_or_in_app_side_effects()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: true,
            telegramText: null,
            telegramContactValidated: true
        );

        var result = await EnqueueTelegramAsync(factory, seed);

        Assert.Equal(NotificationIntentEnqueueState.Invalid, result.State);
        Assert.Empty(await NotificationIntentSeed.ReadIntentsAsync(factory));
        Assert.Empty(await NotificationIntentSeed.ReadNotificationMessagesAsync(factory));
    }

    [Fact]
    public async Task Unapproved_telegram_template_rejects_enqueue_without_side_effects()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: true,
            telegramTemplateApproved: false,
            telegramText: null,
            telegramContactValidated: true
        );

        var result = await EnqueueTelegramAsync(factory, seed);

        Assert.Equal(NotificationIntentEnqueueState.Invalid, result.State);
        Assert.Empty(await NotificationIntentSeed.ReadIntentsAsync(factory));
        Assert.Empty(await NotificationIntentSeed.ReadNotificationMessagesAsync(factory));
    }

    [Fact]
    public async Task Invalid_telegram_key_is_rejected_before_any_channel_is_written()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: true,
            telegramContactValidated: true
        );

        var result = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                TelegramTemplateKey: "   "
            )
        );

        Assert.Equal(NotificationIntentEnqueueState.Invalid, result.State);
        Assert.Empty(await NotificationIntentSeed.ReadIntentsAsync(factory));
        Assert.Empty(await NotificationIntentSeed.ReadNotificationMessagesAsync(factory));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\tapproved")]
    [InlineData("approved\t")]
    public async Task Telegram_template_constraint_rejects_btrim_whitespace_only_and_edge_text(
        string invalidText
    )
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(factory);
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();

        await Assert.ThrowsAsync<PostgresException>(() => InsertTemplateAsync(
            administrator,
            seed.CompanyId,
            $"telegram-invalid-{Guid.NewGuid():N}",
            invalidText
        ));
    }

    [Fact]
    public async Task Telegram_template_constraint_counts_supplementary_plane_code_points()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(factory);
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        const string supplementaryCharacter = "\U0001F600";

        await InsertTemplateAsync(
            administrator,
            seed.CompanyId,
            "telegram-one-supplementary",
            supplementaryCharacter
        );
        await InsertTemplateAsync(
            administrator,
            seed.CompanyId,
            "telegram-4096-supplementary",
            string.Concat(Enumerable.Repeat(supplementaryCharacter, 4096))
        );
        await Assert.ThrowsAsync<PostgresException>(() => InsertTemplateAsync(
            administrator,
            seed.CompanyId,
            "telegram-4097-supplementary",
            string.Concat(Enumerable.Repeat(supplementaryCharacter, 4097))
        ));
    }

    [Fact]
    public async Task Telegram_contact_change_resets_validation_and_repeating_it_is_idempotent()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramContactValidated: true
        );
        var replacement = "987654321";

        await TelegramNotificationSeed.SetContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            replacement
        );
        var changed = await TelegramNotificationSeed.ReadContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId
        );
        Assert.Equal(replacement, changed.Address);
        Assert.Null(changed.PhoneNumber);
        Assert.False(changed.IsValidated);

        await TelegramNotificationSeed.SetContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            replacement
        );
        var repeated = await TelegramNotificationSeed.ReadContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId
        );
        Assert.Equal(changed, repeated);
        Assert.Equal(
            1,
            await NotificationIntentSeed.CountAuditEventsAsync(
                factory,
                "notification.contact.change"
            )
        );
    }

    [Fact]
    public async Task Telegram_contact_rejects_noncanonical_or_out_of_range_private_ids()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(factory);

        foreach (var invalidAddress in new[] { "0", "-1", "12 34", "4503599627370496" })
        {
            await Assert.ThrowsAsync<PostgresException>(() => TelegramNotificationSeed.SetContactAsync(
                factory,
                seed.CompanyId,
                seed.RecipientUserId,
                invalidAddress
            ));
        }

        var contact = await TelegramNotificationSeed.ReadContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId
        );
        Assert.Equal(TelegramNotificationSeed.TelegramAddress, contact.Address);
        Assert.False(contact.IsValidated);
    }

    [Fact]
    public async Task Telegram_preference_audits_only_effective_self_service_transitions()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(factory);

        Assert.Equal(
            NotificationPreferenceState.Updated,
            (await TelegramNotificationSeed.SetPreferenceViaServiceAsync(
                factory,
                seed.RecipientUserId,
                seed.CompanyId,
                optedIn: true
            )).State
        );
        Assert.Equal(
            NotificationPreferenceState.Updated,
            (await TelegramNotificationSeed.SetPreferenceViaServiceAsync(
                factory,
                seed.RecipientUserId,
                seed.CompanyId,
                optedIn: true
            )).State
        );
        Assert.Equal(
            NotificationPreferenceState.Updated,
            (await TelegramNotificationSeed.SetPreferenceViaServiceAsync(
                factory,
                seed.RecipientUserId,
                seed.CompanyId,
                optedIn: false
            )).State
        );
        Assert.Equal(
            2,
            await NotificationIntentSeed.CountAuditEventsAsync(
                factory,
                "notification.preference.change"
            )
        );
        Assert.Equal(
            1,
            await TelegramNotificationSeed.CountTelegramPreferencesAsync(
                factory,
                seed.CompanyId,
                seed.RecipientUserId
            )
        );
    }

    [Fact]
    public async Task Telegram_cross_company_recipient_is_rejected_without_rows_or_messages()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyA = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: true,
            telegramContactValidated: true
        );
        var companyB = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: true,
            telegramContactValidated: true
        );

        var result = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                companyA.CompanyId,
                companyB.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                companyA.InAppTemplateKey,
                TelegramTemplateKey: companyA.TelegramTemplateKey
            )
        );

        Assert.Equal(NotificationIntentEnqueueState.Forbidden, result.State);
        Assert.Empty(await NotificationIntentSeed.ReadIntentsAsync(factory));
        Assert.Empty(await NotificationIntentSeed.ReadNotificationMessagesAsync(factory));
    }

    [Fact]
    public async Task Telegram_preference_for_another_user_and_inactive_membership_are_denied()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(factory);

        var anotherUser = await TelegramNotificationSeed.SetPreferenceViaServiceAsync(
            factory,
            factory.ActorUserId,
            seed.CompanyId,
            optedIn: true
        );
        Assert.Equal(NotificationPreferenceState.Forbidden, anotherUser.State);

        await TelegramNotificationSeed.RevokeMembershipAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId
        );
        var result = await EnqueueTelegramAsync(factory, seed);
        Assert.Equal(NotificationIntentEnqueueState.Forbidden, result.State);
        Assert.Empty(await NotificationIntentSeed.ReadIntentsAsync(factory));
        Assert.Empty(await NotificationIntentSeed.ReadNotificationMessagesAsync(factory));
    }

    [Fact]
    public async Task Telegram_and_in_app_intents_roll_back_when_outbox_enrollment_fails()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: true,
            telegramContactValidated: true
        );
        await TelegramNotificationSeed.RevokeOutboxInsertAsync(factory);

        var result = await EnqueueTelegramAsync(factory, seed);

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
    public async Task Concurrent_duplicate_telegram_events_produce_one_delivery_per_channel()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: true,
            telegramContactValidated: true
        );
        var command = new NotificationIntentCommand(
            seed.CompanyId,
            seed.RecipientUserId,
            NotificationIntentSeed.NewEventKey(),
            seed.InAppTemplateKey,
            TelegramTemplateKey: seed.TelegramTemplateKey
        );

        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(delay =>
                    Task.Delay(delay * 25).ContinueWith(_ =>
                        NotificationIntentSeed.EnqueueAsync(factory, command)
                    ).Unwrap()
                )
        );

        Assert.All(results, result =>
            Assert.Equal(NotificationIntentEnqueueState.Enqueued, result.State));
        Assert.Equal(1, results.Sum(result => result.Telegram!.Created ? 1 : 0));
        Assert.Equal(1, results.Sum(result => result.InApp!.Created ? 1 : 0));
        Assert.Equal(
            results.Single(result => result.Telegram!.Created).Telegram!.DeliveryId,
            results.Single(result => !result.Telegram!.Created).Telegram!.DeliveryId
        );
        Assert.Equal(2, (await NotificationIntentSeed.ReadIntentsAsync(factory)).Count);
        Assert.Equal(2, (await NotificationIntentSeed.ReadNotificationMessagesAsync(factory)).Count);
    }

    private static Task<NotificationEnqueueResult> EnqueueTelegramAsync(
        FoundationApiFactory factory,
        TelegramNotificationCompanySeed seed
    ) => NotificationIntentSeed.EnqueueAsync(
        factory,
        new NotificationIntentCommand(
            seed.CompanyId,
            seed.RecipientUserId,
            NotificationIntentSeed.NewEventKey(),
            seed.InAppTemplateKey,
            TelegramTemplateKey: seed.TelegramTemplateKey
        )
    );

    private static async Task InsertTemplateAsync(
        NpgsqlConnection connection,
        Guid companyId,
        string templateKey,
        string text
    )
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO notifications.notification_templates
                (company_id, channel, template_key, is_approved, telegram_text)
            VALUES (@company_id, 'telegram', @template_key, true, @telegram_text)
            """,
            connection
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("template_key", templateKey);
        command.Parameters.AddWithValue("telegram_text", text);
        await command.ExecuteNonQueryAsync();
    }

    private static void AssertOpaqueDeliveryEnvelope(
        NotificationQueueMessage message,
        Guid[] expectedDeliveryIds,
        string telegramText,
        string telegramAddress
    )
    {
        var envelope = EnvelopeSerializer.Deserialize(message.Body);
        using var payload = JsonDocument.Parse(envelope.Data);
        var serializedData = Encoding.UTF8.GetString(envelope.Data!);
        var properties = payload.RootElement.EnumerateObject().ToArray();
        var deliveryIdProperty = Assert.Single(properties);
        Assert.Equal("deliveryId", deliveryIdProperty.Name);
        Assert.Equal(JsonValueKind.String, deliveryIdProperty.Value.ValueKind);
        Assert.Contains(deliveryIdProperty.Value.GetGuid(), expectedDeliveryIds);
        Assert.Contains("NotificationDeliveryRequested", message.Metadata, StringComparison.Ordinal);
        Assert.DoesNotContain(telegramText, message.Metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(telegramAddress, message.Metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(telegramText, serializedData, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(telegramAddress, serializedData, StringComparison.OrdinalIgnoreCase);
    }
}
