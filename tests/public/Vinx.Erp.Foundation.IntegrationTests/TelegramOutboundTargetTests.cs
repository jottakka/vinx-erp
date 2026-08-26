using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

[Collection("notification-worker")]
public sealed class TelegramOutboundTargetTests
{
    private const string TelegramChannelMigration =
        "20260826120000_AddTelegramNotificationChannel";

    [Fact]
    public async Task Worker_reads_only_the_matching_in_flight_telegram_target_and_no_other_attempt()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: true,
            telegramContactValidated: true
        );
        var claim = await CreateClaimedTelegramAsync(factory, seed);

        var target = await ReadTargetAsWorkerAsync(factory, claim.DeliveryId, claim.AttemptId);
        Assert.Equal(TelegramNotificationSeed.TelegramAddress, target.ChatId);
        Assert.Equal(TelegramNotificationSeed.TelegramText, target.MessageText);

        var staleAttempt = await ReadTargetAsWorkerAsync(factory, claim.DeliveryId, Guid.NewGuid());
        Assert.Null(staleAttempt.ChatId);
        Assert.Null(staleAttempt.MessageText);

        var unrelated = await ReadTargetAsWorkerAsync(factory, Guid.NewGuid(), claim.AttemptId);
        Assert.Null(unrelated.ChatId);
        Assert.Null(unrelated.MessageText);

        var inAppClaim = await CreateClaimedInAppAsync(factory);
        var nonTelegram = await ReadTargetAsWorkerAsync(
            factory,
            inAppClaim.DeliveryId,
            inAppClaim.AttemptId
        );
        Assert.Null(nonTelegram.ChatId);
        Assert.Null(nonTelegram.MessageText);

        await FinalizeAsync(factory, claim, "delivered");
        var terminal = await ReadTargetAsWorkerAsync(factory, claim.DeliveryId, claim.AttemptId);
        Assert.Null(terminal.ChatId);
        Assert.Null(terminal.MessageText);
    }

    [Fact]
    public async Task Target_function_requires_validated_contact_and_is_executable_only_by_worker()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: true,
            telegramContactValidated: false
        );
        var claim = await CreateClaimedTelegramAsync(factory, seed);

        var target = await ReadTargetAsWorkerAsync(factory, claim.DeliveryId, claim.AttemptId);
        Assert.Null(target.ChatId);
        Assert.Null(target.MessageText);

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                has_function_privilege('vinx_worker', 'notifications.get_telegram_notification_delivery_target(uuid, uuid)', 'EXECUTE'),
                has_function_privilege('vinx_runtime', 'notifications.get_telegram_notification_delivery_target(uuid, uuid)', 'EXECUTE'),
                has_function_privilege('public', 'notifications.get_telegram_notification_delivery_target(uuid, uuid)', 'EXECUTE'),
                has_function_privilege('vinx_migrator', 'notifications.get_telegram_notification_delivery_target(uuid, uuid)', 'EXECUTE')
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
    }

    [Fact]
    public async Task Downgrade_to_the_PR1_Telegram_contract_removes_only_the_outbound_target()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: true,
            telegramContactValidated: true
        );
        var original = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                TelegramTemplateKey: seed.TelegramTemplateKey
            )
        );
        Assert.True(original.Telegram!.Created);

        await using (var migrator = factory.Fixture.CreateMigratorDbContext())
        {
            await migrator.Database.MigrateAsync(TelegramChannelMigration);
        }

        await using (var administrator = await factory.Fixture.OpenAdministratorConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT
                    to_regprocedure(
                        'notifications.get_telegram_notification_delivery_target(uuid,uuid)') IS NULL,
                    to_regprocedure(
                        'notifications.enqueue_notification_intent(uuid,uuid,text,text,text,text)') IS NOT NULL,
                    EXISTS (
                        SELECT 1
                        FROM pg_constraint AS constraint_row
                        WHERE constraint_row.conrelid =
                                'notifications.notification_templates'::regclass
                          AND constraint_row.conname = 'ck_notification_templates_telegram_text'),
                    EXISTS (
                        SELECT 1
                        FROM notifications.notification_preferences AS preference
                        WHERE preference.company_id = @company_id
                          AND preference.user_id = @user_id
                          AND preference.channel = 'telegram'),
                    EXISTS (
                        SELECT 1
                        FROM notifications.notification_templates AS template
                        WHERE template.company_id = @company_id
                          AND template.channel = 'telegram'
                          AND template.template_key = @template_key),
                    EXISTS (
                        SELECT 1
                        FROM notifications.notification_recipient_contacts AS contact
                        WHERE contact.company_id = @company_id
                          AND contact.user_id = @user_id
                          AND contact.channel = 'telegram'
                          AND contact.is_validated),
                    EXISTS (
                        SELECT 1
                        FROM notifications.notification_intents AS intent
                        WHERE intent.id = @delivery_id
                          AND intent.channel = 'telegram')
                """,
                administrator
            );
            command.Parameters.AddWithValue("company_id", seed.CompanyId);
            command.Parameters.AddWithValue("user_id", seed.RecipientUserId);
            command.Parameters.AddWithValue("template_key", seed.TelegramTemplateKey);
            command.Parameters.AddWithValue("delivery_id", original.Telegram.DeliveryId!.Value);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            for (var column = 0; column < reader.FieldCount; column++)
            {
                Assert.True(reader.GetBoolean(column));
            }
        }

        var enqueuedAfterDowngrade = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                TelegramTemplateKey: seed.TelegramTemplateKey
            )
        );
        Assert.True(enqueuedAfterDowngrade.Telegram!.Created);
    }

    internal static async Task<TelegramDeliveryClaim> CreateClaimedTelegramAsync(
        FoundationApiFactory factory,
        TelegramNotificationCompanySeed seed
    )
    {
        var enqueued = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                TelegramTemplateKey: seed.TelegramTemplateKey
            )
        );
        Assert.True(enqueued.Telegram!.Created);
        var deliveryId = enqueued.Telegram.DeliveryId!.Value;

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using (var accept = new NpgsqlCommand(
            "SELECT status FROM notifications.accept_notification_delivery(@delivery_id, @correlation_id)",
            administrator
        ))
        {
            accept.Parameters.AddWithValue("delivery_id", deliveryId);
            accept.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
            Assert.Equal("pending_provider", (string)(await accept.ExecuteScalarAsync())!);
        }

        await using var claim = new NpgsqlCommand(
            "SELECT status, attempt_id FROM notifications.claim_notification_delivery(@delivery_id)",
            administrator
        );
        claim.Parameters.AddWithValue("delivery_id", deliveryId);
        await using var reader = await claim.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("provider_in_flight", reader.GetString(0));
        return new TelegramDeliveryClaim(deliveryId, reader.GetGuid(1));
    }

    private static async Task<TelegramDeliveryClaim> CreateClaimedInAppAsync(
        FoundationApiFactory factory
    )
    {
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
        await using (var accept = new NpgsqlCommand(
            "SELECT status FROM notifications.accept_notification_delivery(@delivery_id, @correlation_id)",
            administrator
        ))
        {
            accept.Parameters.AddWithValue("delivery_id", deliveryId);
            accept.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
            Assert.Equal("pending_provider", (string)(await accept.ExecuteScalarAsync())!);
        }

        await using var claim = new NpgsqlCommand(
            "SELECT status, attempt_id FROM notifications.claim_notification_delivery(@delivery_id)",
            administrator
        );
        claim.Parameters.AddWithValue("delivery_id", deliveryId);
        await using var reader = await claim.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("provider_in_flight", reader.GetString(0));
        return new TelegramDeliveryClaim(deliveryId, reader.GetGuid(1));
    }

    private static async Task<TelegramTarget> ReadTargetAsWorkerAsync(
        FoundationApiFactory factory,
        Guid deliveryId,
        Guid attemptId
    )
    {
        await using var worker = new NpgsqlConnection(factory.Fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT chat_id, message_text FROM notifications.get_telegram_notification_delivery_target(@delivery_id, @attempt_id)",
            worker
        );
        command.Parameters.AddWithValue("delivery_id", deliveryId);
        command.Parameters.AddWithValue("attempt_id", attemptId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new TelegramTarget(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1)
        );
    }

    private static async Task FinalizeAsync(
        FoundationApiFactory factory,
        TelegramDeliveryClaim claim,
        string outcome
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM notifications.finalize_notification_delivery(@delivery_id, @attempt_id, @correlation_id, @outcome)",
            administrator
        );
        command.Parameters.AddWithValue("delivery_id", claim.DeliveryId);
        command.Parameters.AddWithValue("attempt_id", claim.AttemptId);
        command.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
        command.Parameters.AddWithValue("outcome", outcome);
        Assert.Equal("delivered", (string)(await command.ExecuteScalarAsync())!);
    }

    internal sealed record TelegramDeliveryClaim(Guid DeliveryId, Guid AttemptId);

    private sealed record TelegramTarget(string? ChatId, string? MessageText);
}
