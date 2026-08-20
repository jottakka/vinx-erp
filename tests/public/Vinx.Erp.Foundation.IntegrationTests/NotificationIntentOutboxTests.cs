using System.Text.Json;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class NotificationIntentOutboxTests
{
    [Fact]
    public async Task Eligible_whatsapp_event_creates_both_intents_and_opaque_messages_after_commit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        var eventKey = NotificationIntentSeed.NewEventKey();

        var result = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                eventKey,
                seed.InAppTemplateKey,
                WhatsAppTemplateKey: seed.WhatsAppTemplateKey,
                DeepLinkSelector: "/paguai/requests/123"
            )
        );

        Assert.Equal(NotificationIntentEnqueueState.Enqueued, result.State);
        Assert.NotNull(result.WhatsApp);
        Assert.True(result.WhatsApp!.Created);
        Assert.NotNull(result.WhatsApp.DeliveryId);
        Assert.NotNull(result.InApp);
        Assert.True(result.InApp!.Created);
        Assert.NotNull(result.InApp.DeliveryId);
        Assert.Equal("pending_delivery", result.InApp.Status);
        Assert.Equal("pending_delivery", result.WhatsApp.Status);

        var intents = await NotificationIntentSeed.ReadIntentsAsync(factory);
        Assert.Equal(2, intents.Count);
        Assert.All(intents, intent => Assert.Equal("pending_delivery", intent.Status));
        Assert.All(intents, intent => Assert.Equal(0, intent.AttemptCount));
        Assert.Equal(
            ["in_app", "whatsapp"],
            [.. intents.Select(intent => intent.Channel).Order()]
        );

        var messages = await NotificationIntentSeed.ReadNotificationMessagesAsync(factory);
        Assert.Equal(2, messages.Count);
        var deliveryIds = new[]
        {
            result.InApp.DeliveryId!.Value,
            result.WhatsApp.DeliveryId!.Value,
        };
        Assert.All(
            messages,
            message => AssertOpaqueDeliveryEnvelope(message, deliveryIds, eventKey)
        );
        Assert.Equal(
            2,
            await NotificationIntentSeed.CountAuditEventsAsync(
                factory,
                "notification.intent.enqueue"
            )
        );
    }

    [Fact]
    public async Task In_app_only_event_creates_single_intent_and_message()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);

        var result = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey
            )
        );

        Assert.Equal(NotificationIntentEnqueueState.Enqueued, result.State);
        Assert.Null(result.WhatsApp);
        Assert.True(result.InApp!.Created);

        var intents = await NotificationIntentSeed.ReadIntentsAsync(factory);
        var intent = Assert.Single(intents);
        Assert.Equal("in_app", intent.Channel);
        var messages = await NotificationIntentSeed.ReadNotificationMessagesAsync(factory);
        Assert.Single(messages);
    }

    [Fact]
    public async Task Opted_out_recipient_keeps_mandatory_in_app_alert_without_whatsapp_message()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: false
        );

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

        Assert.Equal(NotificationIntentEnqueueState.Enqueued, result.State);
        Assert.NotNull(result.WhatsApp);
        Assert.False(result.WhatsApp!.Created);
        Assert.Null(result.WhatsApp.DeliveryId);
        Assert.Equal("suppressed_channel_opt_out", result.WhatsApp.Status);
        Assert.Equal("channel_opt_out", result.WhatsApp.SuppressionReason);
        Assert.True(result.InApp!.Created);

        var intents = await NotificationIntentSeed.ReadIntentsAsync(factory);
        var intent = Assert.Single(intents);
        Assert.Equal("in_app", intent.Channel);
        var messages = await NotificationIntentSeed.ReadNotificationMessagesAsync(factory);
        Assert.Single(messages);
    }

    [Fact]
    public async Task Unapproved_whatsapp_template_rejects_enqueue_without_rows_or_messages()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true,
            whatsAppTemplateApproved: false
        );

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

        Assert.Equal(NotificationIntentEnqueueState.Invalid, result.State);
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
    public async Task Invalid_command_parameters_reject_without_side_effects()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);

        var emptyEventKey = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                "   ",
                seed.InAppTemplateKey
            )
        );
        var relativeDeepLink = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                DeepLinkSelector: "paguai/requests/123"
            )
        );

        Assert.Equal(NotificationIntentEnqueueState.Invalid, emptyEventKey.State);
        Assert.Equal(NotificationIntentEnqueueState.Invalid, relativeDeepLink.State);
        Assert.Empty(await NotificationIntentSeed.ReadIntentsAsync(factory));
        Assert.Empty(await NotificationIntentSeed.ReadNotificationMessagesAsync(factory));
    }

    private static void AssertOpaqueDeliveryEnvelope(
        NotificationQueueMessage message,
        Guid[] expectedDeliveryIds,
        string eventKey
    )
    {
        var envelope = EnvelopeSerializer.Deserialize(message.Body);
        using var payload = JsonDocument.Parse(envelope.Data);
        var properties = payload.RootElement.EnumerateObject().ToArray();
        var deliveryIdProperty = Assert.Single(properties);
        Assert.Equal("deliveryId", deliveryIdProperty.Name);
        Assert.Equal(JsonValueKind.String, deliveryIdProperty.Value.ValueKind);
        var deliveryId = deliveryIdProperty.Value.GetGuid();
        Assert.Contains(deliveryId, expectedDeliveryIds);
        var requested = Assert.IsType<NotificationDeliveryRequested>(
            new SystemTextJsonSerializer(
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            ).ReadFromData(typeof(NotificationDeliveryRequested), envelope)
        );
        Assert.Equal(deliveryId, requested.DeliveryId);
        Assert.Contains(
            "NotificationDeliveryRequested",
            message.Metadata,
            StringComparison.Ordinal
        );
        foreach (
            var forbidden in new[]
            {
                eventKey,
                "alert.in_app",
                "alert.whatsapp",
                "/paguai/requests/123",
                "notification_templates",
                "deep_link",
            }
        )
        {
            Assert.DoesNotContain(
                forbidden,
                message.Metadata,
                StringComparison.OrdinalIgnoreCase
            );
        }
    }
}
