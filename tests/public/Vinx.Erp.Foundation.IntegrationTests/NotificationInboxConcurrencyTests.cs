using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class NotificationInboxConcurrencyTests
{
    [Fact]
    public async Task Concurrent_marks_via_the_real_function_produce_a_single_transition_with_lock_proof()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var deliveryId = await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);
        var actorId = factory.ActorUserId.ToString();

        await using var connectionA = await NotificationInboxSeed.OpenRuntimeConnectionAsync(factory);
        await using var transactionA = await connectionA.BeginTransactionAsync();
        await SetActorAsync(connectionA, transactionA, actorId);
        var statusA = await ExecuteMarkReadAsync(connectionA, transactionA, deliveryId);
        Assert.Equal("read", statusA);

        await using var connectionB = await NotificationInboxSeed.OpenRuntimeConnectionAsync(factory);
        var backendPidB = (int)
            await NotificationInboxSeed.ExecuteScalarAsync(connectionB, "SELECT pg_backend_pid()")!;
        var markReadTask = Task.Run(async () =>
        {
            await using var transactionB = await connectionB.BeginTransactionAsync();
            await SetActorAsync(connectionB, transactionB, actorId);
            var statusB = await ExecuteMarkReadAsync(connectionB, transactionB, deliveryId);
            await transactionB.CommitAsync();
            return statusB;
        });

        await AssertConnectionBWaitsForLockAsync(factory, backendPidB);

        await transactionA.CommitAsync();
        var statusBResult = await markReadTask;

        Assert.Equal("already_read", statusBResult);
        Assert.Equal(1, await NotificationInboxSeed.CountReadAuditsAsync(factory, deliveryId));
        var readState = await NotificationInboxSeed.ReadInboxStateAsync(factory, deliveryId);
        Assert.NotNull(readState.ReadAt);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationInboxService>();
        var count = await service.CountUnreadAsync(
            new ActorContext(factory.ActorUserId, factory.CorrelationId),
            CancellationToken.None
        );
        Assert.Equal(0, count.UnreadCount);
    }

    [Fact]
    public async Task Service_level_concurrent_marks_end_with_a_single_audited_transition()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var deliveryId = await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);
        var actor = new ActorContext(factory.ActorUserId, factory.CorrelationId);

        var results = await Task.WhenAll(
            MarkReadThroughNewScopeAsync(factory, actor, deliveryId),
            MarkReadThroughNewScopeAsync(factory, actor, deliveryId)
        );

        Assert.All(results, result => Assert.Equal(NotificationMarkReadState.Read, result.State));
        Assert.Equal(1, await NotificationInboxSeed.CountReadAuditsAsync(factory, deliveryId));
        var readState = await NotificationInboxSeed.ReadInboxStateAsync(factory, deliveryId);
        Assert.NotNull(readState.ReadAt);
    }

    [Fact]
    public async Task Count_racing_a_mark_observes_valid_values_and_the_final_count_is_exact()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var deliveryId = await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);
        var actor = new ActorContext(factory.ActorUserId, factory.CorrelationId);

        var markTask = MarkReadThroughNewScopeAsync(factory, actor, deliveryId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<NotificationInboxService>();
            var during = await service.CountUnreadAsync(actor, CancellationToken.None);
            Assert.True(during.UnreadCount is 0 or 1);
        }

        await markTask;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<NotificationInboxService>();
            var finalCount = await service.CountUnreadAsync(actor, CancellationToken.None);
            Assert.Equal(0, finalCount.UnreadCount);
        }
    }

    private static async Task<NotificationMarkReadResult> MarkReadThroughNewScopeAsync(
        FoundationApiFactory factory,
        ActorContext actor,
        Guid deliveryId
    )
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationInboxService>();
        return await service.MarkReadAsync(actor, deliveryId, CancellationToken.None);
    }

    private static async Task SetActorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string actorId
    )
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @user_id, true)",
            connection,
            transaction
        );
        command.Parameters.AddWithValue("user_id", actorId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ExecuteMarkReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid deliveryId
    )
    {
        await using var command = new NpgsqlCommand(
            "SELECT status FROM notifications.mark_inbox_notification_read(@delivery_id)",
            connection,
            transaction
        );
        command.Parameters.AddWithValue("delivery_id", deliveryId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertConnectionBWaitsForLockAsync(
        FoundationApiFactory factory,
        int backendPidB
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT wait_event_type FROM pg_stat_activity
                WHERE pid = @backend_pid AND wait_event_type = 'Lock'
                """,
                administrator
            );
            command.Parameters.AddWithValue("backend_pid", backendPidB);
            if (await command.ExecuteScalarAsync() is not null)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Connection B did not wait on the row lock held by connection A.");
    }
}
