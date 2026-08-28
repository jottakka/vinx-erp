using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class NotificationIntentAccessTests
{
    [Fact]
    public async Task Recipient_from_another_company_is_rejected_without_rows_or_messages()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyA = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        var companyB = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );

        var result = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                companyA.CompanyId,
                companyB.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                companyA.InAppTemplateKey,
                WhatsAppTemplateKey: companyA.WhatsAppTemplateKey
            )
        );

        Assert.Equal(NotificationIntentEnqueueState.Forbidden, result.State);
        Assert.Empty(await NotificationIntentSeed.ReadIntentsAsync(factory));
        Assert.Empty(await NotificationIntentSeed.ReadNotificationMessagesAsync(factory));
    }

    [Fact]
    public async Task Runtime_role_cannot_access_intent_tables_directly()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey
            )
        );

        await using var runtime = new NpgsqlConnection(
            factory.Fixture.RuntimeConnectionString
        );
        await runtime.OpenAsync();
        await using var select = new NpgsqlCommand(
            "SELECT count(*) FROM notifications.notification_intents",
            runtime
        );
        var selectFailure = await Assert.ThrowsAsync<PostgresException>(() =>
            select.ExecuteNonQueryAsync()
        );
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, selectFailure.SqlState);

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO notifications.notification_intents (
                id, company_id, recipient_user_id, event_key, channel, template_key,
                status, correlation_id, created_by_user_id)
            VALUES (
                gen_random_uuid(), @company_id, @recipient_id, 'rogue-event', 'in_app', 'rogue-template',
                'pending_delivery', gen_random_uuid(), @recipient_id)
            """,
            runtime
        );
        insert.Parameters.AddWithValue("company_id", seed.CompanyId);
        insert.Parameters.AddWithValue("recipient_id", seed.RecipientUserId);
        var insertFailure = await Assert.ThrowsAsync<PostgresException>(() =>
            insert.ExecuteNonQueryAsync()
        );
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insertFailure.SqlState);

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var privileges = new NpgsqlCommand(
            """
            SELECT
                has_table_privilege('vinx_runtime', 'notifications.notification_intents', 'SELECT'),
                has_table_privilege('vinx_runtime', 'notifications.notification_preferences', 'INSERT'),
                has_function_privilege('vinx_runtime', 'notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text)', 'EXECUTE'),
                has_function_privilege('vinx_runtime', 'notifications.set_channel_preference(uuid, uuid, text, boolean)', 'EXECUTE'),
                has_schema_privilege('vinx_worker', 'notifications', 'USAGE'),
                has_function_privilege('vinx_worker', 'notifications.enqueue_notification_intent(uuid, uuid, text, text, text, text)', 'EXECUTE'),
                has_function_privilege('vinx_worker', 'notifications.enqueue_paguai_draft_created_notification(uuid, uuid, uuid, uuid)', 'EXECUTE')
            """,
            administrator
        );
        await using var reader = await privileges.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        // Desde o worker de entregas (#48) o vinx_worker tem USAGE no schema
        // para executar as funções de claim/finalize; continua sem DML direto.
        Assert.True(reader.GetBoolean(4));
        // #355 mantém a função genérica proibida e libera só a fronteira Pagaí.
        Assert.False(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
    }

    [Fact]
    public async Task Preference_change_for_another_user_is_denied()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);

        var result = await NotificationIntentSeed.SetPreferenceViaServiceAsync(
            factory,
            factory.ActorUserId,
            seed.CompanyId,
            optedIn: true
        );

        Assert.Equal(NotificationPreferenceState.Forbidden, result.State);
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM notifications.notification_preferences",
            administrator
        );
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }
}
