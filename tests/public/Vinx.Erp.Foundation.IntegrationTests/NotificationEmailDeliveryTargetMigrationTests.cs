using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

[Collection("notification-worker")]
public sealed class NotificationEmailDeliveryTargetMigrationTests
{
    private const string BeforeRepairMigration =
        "20260826225942_AddAtendimentoOperationalMetadata";
    private const string RepairMigration =
        "20260827010000_RepairNotificationEmailDeliveryTarget";
    private const string Subject = "Alerta de pagamento";
    private const string BodyHtml = "<p>Um novo rascunho está disponível.</p>";
    private const string DeepLink = "/paguai/requests/123";

    [Fact]
    public async Task Worker_reads_the_exact_target_only_for_the_current_email_attempt()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedEmailPreferenceAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId
        );
        await NotificationIntentSeed.SeedEmailTemplateAsync(
            factory,
            seed.CompanyId,
            Subject,
            BodyHtml
        );
        var enqueued = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                EmailTemplateKey: NotificationIntentSeed.EmailTemplateKey,
                DeepLinkSelector: DeepLink
            )
        );
        var deliveryId = enqueued.Email!.DeliveryId!.Value;

        await AssertWorkerOnlyBoundaryAsync(factory.Fixture);
        var attemptId = await ClaimAsync(factory.Fixture, deliveryId);
        var target = await ReadTargetAsync(factory.Fixture, deliveryId, attemptId);

        Assert.Equal(await ReadRecipientEmailAsync(factory, seed.RecipientUserId), target.Email);
        Assert.Equal(Subject, target.Subject);
        Assert.Equal(BodyHtml, target.BodyHtml);
        Assert.Equal(DeepLink, target.DeepLinkSelector);

        AssertNullTarget(
            await ReadTargetAsync(factory.Fixture, deliveryId, Guid.NewGuid())
        );
        AssertNullTarget(
            await ReadTargetAsync(factory.Fixture, Guid.NewGuid(), attemptId)
        );
        await FinalizeAsync(factory.Fixture, deliveryId, attemptId);
        AssertNullTarget(await ReadTargetAsync(factory.Fixture, deliveryId, attemptId));
    }

    [Fact]
    public async Task Applied_database_upgrades_to_the_repair_and_down_restores_the_worker_only_predecessor()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedEmailPreferenceAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId
        );
        await NotificationIntentSeed.SeedEmailTemplateAsync(
            factory,
            seed.CompanyId,
            Subject,
            BodyHtml
        );
        var enqueued = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                EmailTemplateKey: NotificationIntentSeed.EmailTemplateKey,
                DeepLinkSelector: DeepLink
            )
        );
        var deliveryId = enqueued.Email!.DeliveryId!.Value;

        await using var migrator = factory.Fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeRepairMigration);
        var predecessorDefinition = await ReadFunctionDefinitionAsync(factory.Fixture);
        var attemptId = await ClaimAsync(factory.Fixture, deliveryId);
        var predecessorFailure = await Assert.ThrowsAsync<PostgresException>(() =>
            ReadTargetAsync(factory.Fixture, deliveryId, attemptId)
        );
        Assert.Equal(PostgresErrorCodes.DatatypeMismatch, predecessorFailure.SqlState);
        await AssertWorkerOnlyBoundaryAsync(factory.Fixture);

        await migrator.Database.MigrateAsync(RepairMigration);
        var repairedDefinition = await ReadFunctionDefinitionAsync(factory.Fixture);
        Assert.NotEqual(predecessorDefinition, repairedDefinition);
        Assert.Equal(
            predecessorDefinition,
            repairedDefinition.Replace(
                "user_record.email::text",
                "user_record.email",
                StringComparison.Ordinal
            )
        );
        var repairedTarget = await ReadTargetAsync(factory.Fixture, deliveryId, attemptId);
        Assert.Equal(Subject, repairedTarget.Subject);
        Assert.Equal(BodyHtml, repairedTarget.BodyHtml);
        Assert.Equal(DeepLink, repairedTarget.DeepLinkSelector);
        await AssertWorkerOnlyBoundaryAsync(factory.Fixture);

        await migrator.Database.MigrateAsync(BeforeRepairMigration);
        Assert.Equal(
            predecessorDefinition,
            await ReadFunctionDefinitionAsync(factory.Fixture)
        );
        var restoredFailure = await Assert.ThrowsAsync<PostgresException>(() =>
            ReadTargetAsync(factory.Fixture, deliveryId, attemptId)
        );
        Assert.Equal(PostgresErrorCodes.DatatypeMismatch, restoredFailure.SqlState);
        await AssertWorkerOnlyBoundaryAsync(factory.Fixture);
    }

    private static async Task AssertWorkerOnlyBoundaryAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                has_function_privilege('vinx_worker', 'notifications.get_notification_email_delivery_target(uuid, uuid)', 'EXECUTE'),
                NOT has_function_privilege('vinx_runtime', 'notifications.get_notification_email_delivery_target(uuid, uuid)', 'EXECUTE'),
                NOT has_function_privilege('vinx_migrator', 'notifications.get_notification_email_delivery_target(uuid, uuid)', 'EXECUTE'),
                NOT has_function_privilege('public', 'notifications.get_notification_email_delivery_target(uuid, uuid)', 'EXECUTE'),
                NOT has_table_privilege('vinx_worker', 'foundation.users', 'SELECT'),
                NOT has_table_privilege('vinx_worker', 'notifications.notification_intents', 'SELECT'),
                NOT has_table_privilege('vinx_worker', 'notifications.notification_templates', 'SELECT'),
                function_row.prosecdef,
                pg_get_userbyid(function_row.proowner) = 'vinx_notifications_owner',
                function_row.proconfig = ARRAY['search_path=pg_catalog, notifications, pg_temp']::text[]
            FROM pg_proc AS function_row
            WHERE function_row.oid =
                'notifications.get_notification_email_delivery_target(uuid, uuid)'::regprocedure
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < reader.FieldCount; index++)
        {
            Assert.True(reader.GetBoolean(index));
        }
    }

    private static async Task FinalizeAsync(
        FoundationPostgresFixture fixture,
        Guid deliveryId,
        Guid attemptId
    )
    {
        await using var worker = new NpgsqlConnection(fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT status, finalized
            FROM notifications.finalize_notification_delivery(
                @delivery_id, @attempt_id, @correlation_id, 'delivered')
            """,
            worker
        );
        command.Parameters.AddWithValue("delivery_id", deliveryId);
        command.Parameters.AddWithValue("attempt_id", attemptId);
        command.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("delivered", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
    }

    private static async Task<Guid> ClaimAsync(
        FoundationPostgresFixture fixture,
        Guid deliveryId
    )
    {
        await using var worker = new NpgsqlConnection(fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using (var accept = new NpgsqlCommand(
            "SELECT status FROM notifications.accept_notification_delivery(@delivery_id, @correlation_id)",
            worker
        ))
        {
            accept.Parameters.AddWithValue("delivery_id", deliveryId);
            accept.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
            Assert.Equal("pending_provider", (string)(await accept.ExecuteScalarAsync())!);
        }

        await using var claim = new NpgsqlCommand(
            """
            SELECT status, attempt_id, channel, claimed
            FROM notifications.claim_notification_delivery(@delivery_id)
            """,
            worker
        );
        claim.Parameters.AddWithValue("delivery_id", deliveryId);
        await using var reader = await claim.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("provider_in_flight", reader.GetString(0));
        Assert.Equal("email", reader.GetString(2));
        Assert.True(reader.GetBoolean(3));
        return reader.GetGuid(1);
    }

    private static async Task<EmailTarget> ReadTargetAsync(
        FoundationPostgresFixture fixture,
        Guid deliveryId,
        Guid attemptId
    )
    {
        await using var worker = new NpgsqlConnection(fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT email, subject, body_html, deep_link_selector
            FROM notifications.get_notification_email_delivery_target(@delivery_id, @attempt_id)
            """,
            worker
        );
        command.Parameters.AddWithValue("delivery_id", deliveryId);
        command.Parameters.AddWithValue("attempt_id", attemptId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new EmailTarget(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3)
        );
    }

    private static async Task<string> ReadRecipientEmailAsync(
        FoundationApiFactory factory,
        Guid recipientUserId
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT email::text FROM foundation.users WHERE id = @recipient_user_id",
            administrator
        );
        command.Parameters.AddWithValue("recipient_user_id", recipientUserId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadFunctionDefinitionAsync(
        FoundationPostgresFixture fixture
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_get_functiondef(
                'notifications.get_notification_email_delivery_target(uuid, uuid)'::regprocedure)
            """,
            administrator
        );
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static void AssertNullTarget(EmailTarget target)
    {
        Assert.Null(target.Email);
        Assert.Null(target.Subject);
        Assert.Null(target.BodyHtml);
        Assert.Null(target.DeepLinkSelector);
    }

    private sealed record EmailTarget(
        string? Email,
        string? Subject,
        string? BodyHtml,
        string? DeepLinkSelector
    );
}
