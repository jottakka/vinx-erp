extern alias workerhost;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Notifications;
using Wolverine;
using Xunit;
using WorkerHostTypes = workerhost::Vinx.Erp.WorkerHost;

namespace Vinx.Erp.Foundation.IntegrationTests;

[Collection("notification-worker")]
public sealed class NotificationWorkerSecurityTests
{
    [Fact]
    public async Task Invalid_envelope_is_dead_lettered_without_provider_call()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        FakeNotificationProvider.Reset();
        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            useFakeWhatsAppProvider: true
        );
        using var client = worker.CreateClient();
        await using var scope = worker.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationDeliveryWorkerService>();
        var handler = new WorkerHostTypes.NotificationDeliveryRequestedHandler(
            service,
            NullLogger<WorkerHostTypes.NotificationDeliveryRequestedHandler>.Instance
        );

        var emptyDelivery = new Envelope(new NotificationDeliveryRequested(Guid.Empty))
        {
            CorrelationId = Guid.NewGuid().ToString("D")
        };
        var missingCorrelation = new Envelope(new NotificationDeliveryRequested(Guid.NewGuid()));

        await Assert.ThrowsAsync<WorkerHostTypes.NotificationDeliveryDeadLetterException>(() =>
            handler.Handle(
                (NotificationDeliveryRequested)emptyDelivery.Message!,
                emptyDelivery,
                CancellationToken.None
            )
        );
        await Assert.ThrowsAsync<WorkerHostTypes.NotificationDeliveryDeadLetterException>(() =>
            handler.Handle(
                (NotificationDeliveryRequested)missingCorrelation.Message!,
                missingCorrelation,
                CancellationToken.None
            )
        );
        Assert.Equal(0, FakeNotificationProvider.Calls);
    }

    [Fact]
    public async Task Missing_provider_channel_is_dead_lettered_redacted_without_claiming_the_delivery()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
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
        var deliveryId = enqueued.WhatsApp!.DeliveryId!.Value;

        // Desde o adapter Meta (#28) o wiring de produção registra o provider
        // do canal WhatsApp; sem número cadastrado o resultado é permanente
        // sem nenhuma chamada externa — e nada vai para a DLQ.
        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "provider_failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("provider_failed", state.Status);
        Assert.False(await HasDeadLettersAsync(factory));
    }

    private static async Task<bool> HasDeadLettersAsync(FoundationApiFactory factory) =>
        (await NotificationIntentSeed.ReadWorkerDeadLetterBodiesAsync(factory)).Count > 0;

    [Fact]
    public async Task Worker_without_function_grants_fails_closed_without_losing_the_delivery()
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
        var deliveryId = enqueued.InApp!.DeliveryId!.Value;

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var revoke = new NpgsqlCommand(
            """
            REVOKE EXECUTE ON FUNCTION notifications.accept_notification_delivery(uuid, uuid) FROM vinx_worker;
            REVOKE EXECUTE ON FUNCTION notifications.claim_notification_delivery(uuid) FROM vinx_worker;
            REVOKE EXECUTE ON FUNCTION notifications.finalize_notification_delivery(uuid, uuid, uuid, text) FROM vinx_worker;
            REVOKE EXECUTE ON FUNCTION notifications.release_notification_delivery(uuid, uuid) FROM vinx_worker;
            """,
            administrator
        );
        await revoke.ExecuteNonQueryAsync();

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString
        );
        using var client = worker.CreateClient();
        await Task.Delay(TimeSpan.FromSeconds(3));

        var state = await NotificationIntentSeed.ReadDeliveryAsync(factory, deliveryId);
        Assert.Equal("pending_delivery", state.Status);
        using var health = await client.GetAsync("/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Worker_functions_are_executable_only_by_the_worker_role()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                has_function_privilege('vinx_worker', 'notifications.accept_notification_delivery(uuid, uuid)', 'EXECUTE'),
                has_function_privilege('vinx_worker', 'notifications.claim_notification_delivery(uuid)', 'EXECUTE'),
                has_function_privilege('vinx_worker', 'notifications.finalize_notification_delivery(uuid, uuid, uuid, text)', 'EXECUTE'),
                has_function_privilege('vinx_worker', 'notifications.release_notification_delivery(uuid, uuid)', 'EXECUTE'),
                has_function_privilege('vinx_runtime', 'notifications.accept_notification_delivery(uuid, uuid)', 'EXECUTE'),
                has_function_privilege('vinx_runtime', 'notifications.claim_notification_delivery(uuid)', 'EXECUTE'),
                has_function_privilege('vinx_runtime', 'notifications.finalize_notification_delivery(uuid, uuid, uuid, text)', 'EXECUTE'),
                has_function_privilege('vinx_runtime', 'notifications.release_notification_delivery(uuid, uuid)', 'EXECUTE'),
                has_table_privilege('vinx_worker', 'notifications.notification_intents', 'SELECT'),
                has_table_privilege('vinx_runtime', 'notifications.notification_intents', 'UPDATE')
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
        Assert.False(reader.GetBoolean(8));
        Assert.False(reader.GetBoolean(9));
    }

    internal static async Task<IReadOnlyList<byte[]>> WaitDeadLetterBodyAsync(
        FoundationApiFactory factory,
        int minimum
    )
    {
        var deadline = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(30));
        IReadOnlyList<byte[]> bodies = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            bodies = await NotificationIntentSeed.ReadWorkerDeadLetterBodiesAsync(factory);
            if (bodies.Count >= minimum)
            {
                return bodies;
            }

            await Task.Delay(250);
        }

        return bodies;
    }
}
