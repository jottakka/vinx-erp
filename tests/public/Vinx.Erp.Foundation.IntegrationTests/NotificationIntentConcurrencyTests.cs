using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class NotificationIntentConcurrencyTests
{
    [Fact]
    public async Task Concurrent_duplicate_events_produce_one_delivery_per_channel()
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

        // Stagger the enqueues slightly: the race against the deduplicated
        // function stays real, but a thundering herd does not add load to the
        // shared container.
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(delay =>
                    Task.Delay(delay * 25).ContinueWith(_ =>
                        NotificationIntentSeed.EnqueueAsync(factory, command)
                    )
                    .Unwrap()
                )
        );

        Assert.All(results, result =>
            Assert.Equal(NotificationIntentEnqueueState.Enqueued, result.State)
        );
        Assert.Equal(
            1,
            results.Sum(result => result.WhatsApp!.Created ? 1 : 0)
        );
        Assert.Equal(1, results.Sum(result => result.InApp!.Created ? 1 : 0));
        Assert.Equal(
            results.Single(result => result.WhatsApp!.Created).WhatsApp!.DeliveryId,
            results.Single(result => !result.WhatsApp!.Created).WhatsApp!.DeliveryId
        );
        Assert.Equal(
            results.Single(result => result.InApp!.Created).InApp!.DeliveryId,
            results.Single(result => !result.InApp!.Created).InApp!.DeliveryId
        );

        var intents = await NotificationIntentSeed.ReadIntentsAsync(factory);
        Assert.Equal(2, intents.Count);
        Assert.Equal(
            ["in_app", "whatsapp"],
            [.. intents.Select(intent => intent.Channel).Order()]
        );
        var messages = await NotificationIntentSeed.ReadNotificationMessagesAsync(factory);
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task Distinct_events_for_same_recipient_create_separate_deliveries()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );

        var first = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                WhatsAppTemplateKey: seed.WhatsAppTemplateKey
            )
        );
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

        Assert.True(first.WhatsApp!.Created);
        Assert.True(second.WhatsApp!.Created);
        Assert.NotEqual(first.WhatsApp.DeliveryId, second.WhatsApp.DeliveryId);

        var intents = await NotificationIntentSeed.ReadIntentsAsync(factory);
        Assert.Equal(4, intents.Count);
        Assert.Equal(4, intents.Select(intent => intent.Id).Distinct().Count());
        var messages = await NotificationIntentSeed.ReadNotificationMessagesAsync(factory);
        Assert.Equal(4, messages.Count);
    }
}
