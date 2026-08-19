using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

[Collection("notification-worker")]
public sealed class NotificationWorkerTests
{
    [Fact]
    public async Task In_app_delivery_concludes_once_with_audit_and_outcome_metric()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        var enqueued = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey
            )
        );
        Assert.True(enqueued.InApp!.Created);
        var deliveryId = enqueued.InApp.DeliveryId!.Value;
        using var metrics = OutcomeMetricsListener.Start();

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            debugLogPath: "/tmp/vinx-worker-inapp.log"
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "delivered",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("delivered", state.Status);
        Assert.True(state.Resolved);
        Assert.Null(state.FailureReason);
        Assert.Equal(1, state.AttemptCount);
        Assert.Equal(
            1,
            await NotificationIntentSeed.CountAuditEventsAsync(
                factory,
                "notification.delivery.ingress"
            )
        );
        Assert.Equal(
            1,
            await NotificationIntentSeed.CountAuditEventsAsync(
                factory,
                "notification.delivery.provider"
            )
        );
        var outcome = Assert.Single(metrics.Outcomes);
        Assert.Equal("in_app", outcome.Channel);
        Assert.Equal("delivered", outcome.Outcome);
    }

    [Fact]
    public async Task WhatsApp_delivery_with_delivered_provider_concludes_once()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        FakeNotificationProvider.Reset();
        var enqueued = await EnqueueWhatsAppAsync(factory, seed);
        var deliveryId = enqueued.WhatsApp!.DeliveryId!.Value;

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            useFakeWhatsAppProvider: true
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "delivered",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("delivered", state.Status);
        Assert.Equal(1, FakeNotificationProvider.Calls);
        Assert.Equal(1, state.AttemptCount);
    }

    [Fact]
    public async Task WhatsApp_timeout_maps_to_terminal_ambiguous_without_retry()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        FakeNotificationProvider.Reset(resultKind: NotificationProviderResultKind.Ambiguous);
        var enqueued = await EnqueueWhatsAppAsync(factory, seed);
        var deliveryId = enqueued.WhatsApp!.DeliveryId!.Value;

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            useFakeWhatsAppProvider: true
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "provider_ambiguous",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("provider_ambiguous", state.Status);
        Assert.True(state.Resolved);
        Assert.Equal("provider_ambiguous", state.FailureReason);
        // Ambíguo é terminal: exatamente uma chamada, sem retry cego.
        Assert.Equal(1, FakeNotificationProvider.Calls);
    }

    [Fact]
    public async Task WhatsApp_rejection_maps_to_terminal_provider_failed()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        FakeNotificationProvider.Reset(resultKind: NotificationProviderResultKind.Rejected);
        var enqueued = await EnqueueWhatsAppAsync(factory, seed);
        var deliveryId = enqueued.WhatsApp!.DeliveryId!.Value;

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            useFakeWhatsAppProvider: true
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "provider_failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("provider_failed", state.Status);
        Assert.Equal("provider_rejected", state.FailureReason);
        Assert.Equal(1, FakeNotificationProvider.Calls);
    }

    [Fact]
    public async Task Delivery_in_flight_by_another_attempt_is_skipped_without_provider_call()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        FakeNotificationProvider.Reset();
        var enqueued = await EnqueueWhatsAppAsync(factory, seed);
        var deliveryId = enqueued.WhatsApp!.DeliveryId!.Value;

        // Simula outra tentativa: ingresso concluído e lease reivindicado.
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var accept = new NpgsqlCommand(
            "SELECT status FROM notifications.accept_notification_delivery(@delivery_id, @correlation_id)",
            administrator
        );
        accept.Parameters.AddWithValue("delivery_id", deliveryId);
        accept.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
        Assert.Equal("pending_provider", (string)(await accept.ExecuteScalarAsync())!);
        await using var claim = new NpgsqlCommand(
            "SELECT status FROM notifications.claim_notification_delivery(@delivery_id)",
            administrator
        );
        claim.Parameters.AddWithValue("delivery_id", deliveryId);
        Assert.Equal("provider_in_flight", (string)(await claim.ExecuteScalarAsync())!);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            useFakeWhatsAppProvider: true
        );
        using var client = worker.CreateClient();
        await Task.Delay(TimeSpan.FromSeconds(3));

        var state = await NotificationIntentSeed.ReadDeliveryAsync(factory, deliveryId);
        Assert.Equal("provider_in_flight", state.Status);
        Assert.Equal(0, FakeNotificationProvider.Calls);
    }

    [Fact]
    public async Task Concurrent_worker_services_produce_one_delivery_and_one_provider_call()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        FakeNotificationProvider.Reset();
        var enqueued = await EnqueueWhatsAppAsync(factory, seed);
        var deliveryId = enqueued.WhatsApp!.DeliveryId!.Value;
        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            useFakeWhatsAppProvider: true
        );
        using var client = worker.CreateClient();
        var correlationId = Guid.NewGuid();

        var results = await Task.WhenAll(
            Task.Delay(0).ContinueWith(_ => DeliverDirectAsync(worker, deliveryId, correlationId))
                .Unwrap(),
            Task.Delay(25).ContinueWith(_ => DeliverDirectAsync(worker, deliveryId, correlationId))
                .Unwrap()
        );

        Assert.Equal(1, FakeNotificationProvider.Calls);
        var winner = Assert.Single(results, result => result.Finalized);
        Assert.Equal("delivered", winner.Status);
        // O perdedor da corrida vê o lease alheio e retorna skip sem
        // confirmação: status intermediário legítimo, nunca regressão.
        var loser = Assert.Single(results, result => !result.Finalized);
        Assert.Contains(loser.Status, ["delivered", "provider_in_flight"]);
        var state = await NotificationIntentSeed.ReadDeliveryAsync(factory, deliveryId);
        Assert.Equal("delivered", state.Status);
        Assert.Equal(1, state.AttemptCount);
    }

    [Fact]
    public async Task Transient_provider_errors_release_the_lease_and_exhaust_retries_without_false_confirmation()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        FakeNotificationProvider.Reset(throwTransient: true);
        var enqueued = await EnqueueWhatsAppAsync(factory, seed);
        var deliveryId = enqueued.WhatsApp!.DeliveryId!.Value;

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            useFakeWhatsAppProvider: true
        );
        using var client = worker.CreateClient();

        var deadline = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(90));
        while (DateTimeOffset.UtcNow < deadline && FakeNotificationProvider.Calls < 4)
        {
            await Task.Delay(500);
        }

        Assert.Equal(4, FakeNotificationProvider.Calls);
        var state = await NotificationIntentSeed.ReadDeliveryAsync(factory, deliveryId);
        Assert.Equal("pending_provider", state.Status);
        Assert.False(state.Resolved);
        Assert.Equal(4, state.AttemptCount);

        var bodies = await NotificationWorkerSecurityTests.WaitDeadLetterBodyAsync(factory, 1);
        Assert.NotEmpty(bodies);
    }

    private static async Task<NotificationEnqueueResult> EnqueueWhatsAppAsync(
        FoundationApiFactory factory,
        NotificationCompanySeed seed
    )
    {
        var enqueued = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                WhatsAppTemplateKey: seed.WhatsAppTemplateKey
            )
        );
        Assert.True(enqueued.WhatsApp!.Created);
        return enqueued;
    }

    private static async Task<NotificationDeliveryResult> DeliverDirectAsync(
        NotificationWorkerHostFactory worker,
        Guid deliveryId,
        Guid correlationId
    )
    {
        await using var scope = worker.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationDeliveryWorkerService>();
        return await service.DeliverAsync(deliveryId, correlationId, CancellationToken.None);
    }

    private sealed record OutcomeMeasurement(string Channel, string Outcome);

    private sealed class OutcomeMetricsListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<OutcomeMeasurement> _outcomes = [];

        private OutcomeMetricsListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == NotificationDeliveryMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, value, tags, _) =>
                {
                    if (instrument.Name != "notification.delivery.outcomes" || value <= 0)
                    {
                        return;
                    }

                    string? channel = null;
                    string? outcome = null;
                    foreach (var pair in tags)
                    {
                        if (pair.Key == "channel")
                        {
                            channel = pair.Value?.ToString();
                        }
                        else if (pair.Key == "outcome")
                        {
                            outcome = pair.Value?.ToString();
                        }
                    }

                    lock (_outcomes)
                    {
                        _outcomes.Add(new OutcomeMeasurement(channel ?? "unknown", outcome ?? "unknown"));
                    }
                }
            );
            _listener.Start();
        }

        public IReadOnlyList<OutcomeMeasurement> Outcomes
        {
            get
            {
                lock (_outcomes)
                {
                    return _outcomes.ToList();
                }
            }
        }

        public static OutcomeMetricsListener Start() => new();

        public void Dispose() => _listener.Dispose();
    }
}
