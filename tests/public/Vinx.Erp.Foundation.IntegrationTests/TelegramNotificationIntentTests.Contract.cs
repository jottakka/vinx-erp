using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed partial class TelegramNotificationIntentTests
{
    private const string BeforeTelegramNotificationChannel =
        "20260824235249_AddAiReadCapability";
    private const string TelegramNotificationChannelMigration =
        "20260826120000_AddTelegramNotificationChannel";

    private sealed record LegacyNotificationState(
        Guid CompanyId,
        Guid RecipientUserId,
        Guid InAppIntentId,
        Guid WhatsAppIntentId,
        string InAppTemplateKey,
        string WhatsAppTemplateKey,
        string PhoneNumber
    );

    private sealed record RecipientContactColumns(
        string Channel,
        string? PhoneNumber,
        string? Address
    );

    [Fact]
    public async Task Telegram_down_restores_predecessor_contract_and_preserves_legacy_rows()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var legacy = await SeedLegacyNotificationStateAsync(fixture);

        await using (var migrator = fixture.CreateMigratorDbContext())
        {
            await migrator.Database.MigrateAsync(BeforeTelegramNotificationChannel);
        }

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        {
            await AssertLegacyRowsPreservedAsync(administrator, legacy);
            await AssertPredecessorSchemaAsync(administrator);
            await AssertPredecessorWritesAreRejectedAsync(administrator, legacy);
            await AssertRestoredFunctionSecurityAsync(administrator);
        }

        await AssertRestoredFunctionsAreCallableAsync(fixture, legacy);
    }

    [Fact]
    public async Task Telegram_down_refuses_state_without_changing_history_or_legacy_rows()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var legacy = await SeedLegacyNotificationStateAsync(fixture);
        await InsertTelegramTemplateStateAsync(fixture, legacy);

        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.Database.MigrateAsync(BeforeTelegramNotificationChannel)
        );

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM "__EFMigrationsHistory"
                    WHERE migration_id = @migration_id),
                EXISTS (
                    SELECT 1
                    FROM notifications.notification_templates
                    WHERE company_id = @company_id
                      AND channel = 'telegram'
                      AND template_key = 'legacy.telegram'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'notifications'
                      AND table_name = 'notification_recipient_contacts'
                      AND column_name = 'address'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'notifications'
                      AND table_name = 'notification_templates'
                      AND column_name = 'telegram_text'),
                to_regprocedure(
                    'notifications.set_recipient_contact(uuid,uuid,text,text)') IS NOT NULL
            """,
            administrator
        );
        command.Parameters.AddWithValue("migration_id", TelegramNotificationChannelMigration);
        command.Parameters.AddWithValue("company_id", legacy.CompanyId);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
            Assert.True(reader.GetBoolean(4));
        }

        await AssertLegacyRowsPreservedAsync(administrator, legacy);
    }

    [Fact]
    public async Task Telegram_preference_and_contact_require_an_actor_context()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(factory);

        var preferenceException = await Assert.ThrowsAsync<PostgresException>(() =>
            CallRuntimeFunctionAsync(
                factory,
                null,
                """
                SELECT notifications.set_channel_preference(
                    @company_id, @user_id, 'telegram', true)
                """,
                ("company_id", seed.CompanyId),
                ("user_id", seed.RecipientUserId)
            )
        );
        var contactException = await Assert.ThrowsAsync<PostgresException>(() =>
            CallRuntimeFunctionAsync(
                factory,
                null,
                """
                SELECT notifications.set_recipient_contact(
                    @company_id, @user_id, 'telegram', @address)
                """,
                ("company_id", seed.CompanyId),
                ("user_id", seed.RecipientUserId),
                ("address", "987654321")
            )
        );

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, preferenceException.SqlState);
        Assert.Equal("notification actor is required", preferenceException.MessageText);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, contactException.SqlState);
        Assert.Equal("notification actor is required", contactException.MessageText);
        Assert.Equal(
            0L,
            await TelegramNotificationSeed.CountTelegramPreferencesAsync(
                factory,
                seed.CompanyId,
                seed.RecipientUserId
            )
        );
        Assert.Equal(
            TelegramNotificationSeed.TelegramAddress,
            (await TelegramNotificationSeed.ReadContactAsync(
                factory,
                seed.CompanyId,
                seed.RecipientUserId
            )).Address
        );
        Assert.Equal(
            0L,
            await NotificationIntentSeed.CountAuditEventsAsync(
                factory,
                "notification.preference.change"
            )
        );
        Assert.Equal(
            0L,
            await NotificationIntentSeed.CountAuditEventsAsync(
                factory,
                "notification.contact.change"
            )
        );
    }

    [Fact]
    public async Task Telegram_actor_a_cannot_change_target_b_preference_or_contact()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(factory);
        await AddActiveMembershipAsync(factory, seed.CompanyId, factory.ActorUserId);

        Assert.NotEqual(factory.ActorUserId, seed.RecipientUserId);
        var preferenceException = await Assert.ThrowsAsync<PostgresException>(() =>
            CallRuntimeFunctionAsync(
                factory,
                factory.ActorUserId,
                """
                SELECT notifications.set_channel_preference(
                    @company_id, @user_id, 'telegram', true)
                """,
                ("company_id", seed.CompanyId),
                ("user_id", seed.RecipientUserId)
            )
        );
        var contactException = await Assert.ThrowsAsync<PostgresException>(() =>
            CallRuntimeFunctionAsync(
                factory,
                factory.ActorUserId,
                """
                SELECT notifications.set_recipient_contact(
                    @company_id, @user_id, 'telegram', @address)
                """,
                ("company_id", seed.CompanyId),
                ("user_id", seed.RecipientUserId),
                ("address", "987654321")
            )
        );

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, preferenceException.SqlState);
        Assert.Equal(
            "notification preference changes are self-service",
            preferenceException.MessageText
        );
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, contactException.SqlState);
        Assert.Equal(
            "notification preference changes are self-service",
            contactException.MessageText
        );
        Assert.Equal(
            0L,
            await TelegramNotificationSeed.CountTelegramPreferencesAsync(
                factory,
                seed.CompanyId,
                seed.RecipientUserId
            )
        );
        Assert.Equal(
            TelegramNotificationSeed.TelegramAddress,
            (await TelegramNotificationSeed.ReadContactAsync(
                factory,
                seed.CompanyId,
                seed.RecipientUserId
            )).Address
        );
        Assert.Equal(
            0L,
            await NotificationIntentSeed.CountAuditEventsAsync(
                factory,
                "notification.preference.change"
            )
        );
        Assert.Equal(
            0L,
            await NotificationIntentSeed.CountAuditEventsAsync(
                factory,
                "notification.contact.change"
            )
        );
    }

    [Fact]
    public async Task Telegram_contact_constraint_rejects_mixed_or_empty_address_columns()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        var invalidRows = new[]
        {
            new RecipientContactColumns("whatsapp", "+5511999999999", "123456789"),
            new RecipientContactColumns("whatsapp", null, null),
            new RecipientContactColumns("telegram", "+5511999999999", "123456789"),
            new RecipientContactColumns("telegram", null, null),
        };
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();

        foreach (var invalidRow in invalidRows)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    administrator,
                    """
                    INSERT INTO notifications.notification_recipient_contacts (
                        company_id, user_id, channel, phone_number, address, updated_by_user_id)
                    VALUES (
                        @company_id, @user_id, @channel, @phone_number, @address, @updated_by_user_id)
                    """,
                    ("company_id", seed.CompanyId),
                    ("user_id", seed.RecipientUserId),
                    ("channel", invalidRow.Channel),
                    ("phone_number", (object?)invalidRow.PhoneNumber ?? DBNull.Value),
                    ("address", (object?)invalidRow.Address ?? DBNull.Value),
                    ("updated_by_user_id", factory.ActorUserId)
                )
            );

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        await using var count = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM notifications.notification_recipient_contacts
            WHERE company_id = @company_id AND user_id = @user_id
            """,
            administrator
        );
        count.Parameters.AddWithValue("company_id", seed.CompanyId);
        count.Parameters.AddWithValue("user_id", seed.RecipientUserId);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Concurrent_identical_telegram_contact_updates_leave_one_row_and_one_audit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramContactValidated: true
        );
        const string replacement = "987654321";
        var releaseUpdates = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var readyUpdates = 0;

        async Task UpdateAsync()
        {
            try
            {
                await using var runtime = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString);
                await runtime.OpenAsync();
                await using var transaction = await runtime.BeginTransactionAsync();
                await SetRuntimeActorContextAsync(runtime, transaction, seed.RecipientUserId);
                if (Interlocked.Increment(ref readyUpdates) == 2)
                {
                    releaseUpdates.TrySetResult(true);
                }

                await releaseUpdates.Task;
                await ExecuteNonQueryAsync(
                    runtime,
                    transaction,
                    """
                    SELECT notifications.set_recipient_contact(
                        @company_id, @user_id, 'telegram', @address)
                    """,
                    ("company_id", seed.CompanyId),
                    ("user_id", seed.RecipientUserId),
                    ("address", replacement)
                );
                await transaction.CommitAsync();
            }
            catch (Exception exception)
            {
                releaseUpdates.TrySetException(exception);
                throw;
            }
        }

        await Task.WhenAll(UpdateAsync(), UpdateAsync());

        var contact = await TelegramNotificationSeed.ReadContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId
        );
        Assert.Equal(replacement, contact.Address);
        Assert.Null(contact.PhoneNumber);
        Assert.False(contact.IsValidated);
        Assert.Equal(
            1L,
            await CountTelegramContactsAsync(factory, seed.CompanyId, seed.RecipientUserId)
        );
        Assert.Equal(
            1L,
            await NotificationIntentSeed.CountAuditEventsAsync(
                factory,
                "notification.contact.change"
            )
        );
    }

    private static async Task<LegacyNotificationState> SeedLegacyNotificationStateAsync(
        FoundationPostgresFixture fixture
    )
    {
        var companyId = Guid.NewGuid();
        var recipientUserId = Guid.NewGuid();
        var inAppIntentId = Guid.NewGuid();
        var whatsAppIntentId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        const string inAppTemplateKey = "legacy.in_app";
        const string whatsAppTemplateKey = "legacy.whatsapp";
        const string phoneNumber = "+5511999999999";
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();

        await ExecuteNonQueryAsync(
            administrator,
            """
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES (@user_id, @display_name, @email, clock_timestamp());
            INSERT INTO foundation.groups (
                id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (
                @group_id, @group_name, @user_id, @user_id, @group_request_id,
                clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.companies (
                id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (
                @company_id, @group_id, @company_name, @user_id, @user_id, @company_request_id,
                clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.memberships (
                id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (
                gen_random_uuid(), @company_id, @user_id, @user_id, @user_id, @membership_request_id,
                clock_timestamp(), clock_timestamp());
            INSERT INTO notifications.notification_templates (
                company_id, channel, template_key, is_approved)
            VALUES
                (@company_id, 'in_app', @in_app_template_key, true),
                (@company_id, 'whatsapp', @whatsapp_template_key, true);
            INSERT INTO notifications.notification_preferences (
                company_id, user_id, channel, opted_in, updated_by_user_id)
            VALUES (@company_id, @user_id, 'whatsapp', true, @user_id);
            INSERT INTO notifications.notification_recipient_contacts (
                company_id, user_id, channel, phone_number, address, is_validated, updated_by_user_id)
            VALUES (
                @company_id, @user_id, 'whatsapp', @phone_number, NULL, true, @user_id);
            INSERT INTO notifications.notification_intents (
                id, company_id, recipient_user_id, event_key, channel, template_key,
                status, correlation_id, created_by_user_id)
            VALUES
                (
                    @in_app_intent_id, @company_id, @user_id, 'legacy-event-in-app', 'in_app',
                    @in_app_template_key, 'pending_delivery', @in_app_correlation_id, @user_id),
                (
                    @whatsapp_intent_id, @company_id, @user_id, 'legacy-event-whatsapp', 'whatsapp',
                    @whatsapp_template_key, 'pending_delivery', @whatsapp_correlation_id, @user_id);
            """,
            ("user_id", recipientUserId),
            ("display_name", $"legacy-recipient-{suffix}"),
            ("email", $"legacy-recipient-{suffix}@example.test"),
            ("group_id", Guid.NewGuid()),
            ("group_name", $"legacy-group-{suffix}"),
            ("group_request_id", Guid.NewGuid()),
            ("company_id", companyId),
            ("company_name", $"legacy-company-{suffix}"),
            ("company_request_id", Guid.NewGuid()),
            ("membership_request_id", Guid.NewGuid()),
            ("in_app_template_key", inAppTemplateKey),
            ("whatsapp_template_key", whatsAppTemplateKey),
            ("phone_number", phoneNumber),
            ("in_app_intent_id", inAppIntentId),
            ("in_app_correlation_id", Guid.NewGuid()),
            ("whatsapp_intent_id", whatsAppIntentId),
            ("whatsapp_correlation_id", Guid.NewGuid())
        );

        return new LegacyNotificationState(
            companyId,
            recipientUserId,
            inAppIntentId,
            whatsAppIntentId,
            inAppTemplateKey,
            whatsAppTemplateKey,
            phoneNumber
        );
    }

    private static async Task InsertTelegramTemplateStateAsync(
        FoundationPostgresFixture fixture,
        LegacyNotificationState legacy
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await ExecuteNonQueryAsync(
            administrator,
            """
            INSERT INTO notifications.notification_templates (
                company_id, channel, template_key, is_approved, telegram_text)
            VALUES (@company_id, 'telegram', 'legacy.telegram', true, 'Telegram aprovado')
            """,
            ("company_id", legacy.CompanyId)
        );
    }

    private static async Task AssertLegacyRowsPreservedAsync(
        NpgsqlConnection administrator,
        LegacyNotificationState legacy
    )
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM notifications.notification_preferences AS preference
                    WHERE preference.company_id = @company_id
                      AND preference.user_id = @user_id
                      AND preference.channel = 'whatsapp'
                      AND preference.opted_in),
                EXISTS (
                    SELECT 1
                    FROM notifications.notification_recipient_contacts AS contact
                    WHERE contact.company_id = @company_id
                      AND contact.user_id = @user_id
                      AND contact.channel = 'whatsapp'
                      AND contact.phone_number = @phone_number
                      AND contact.is_validated),
                EXISTS (
                    SELECT 1
                    FROM notifications.notification_templates AS template
                    WHERE template.company_id = @company_id
                      AND template.channel = 'in_app'
                      AND template.template_key = @in_app_template_key
                      AND template.is_approved),
                EXISTS (
                    SELECT 1
                    FROM notifications.notification_templates AS template
                    WHERE template.company_id = @company_id
                      AND template.channel = 'whatsapp'
                      AND template.template_key = @whatsapp_template_key
                      AND template.is_approved),
                EXISTS (
                    SELECT 1
                    FROM notifications.notification_intents AS intent
                    WHERE intent.id = @in_app_intent_id
                      AND intent.channel = 'in_app'
                      AND intent.status = 'pending_delivery'),
                EXISTS (
                    SELECT 1
                    FROM notifications.notification_intents AS intent
                    WHERE intent.id = @whatsapp_intent_id
                      AND intent.channel = 'whatsapp'
                      AND intent.status = 'pending_delivery')
            """,
            administrator
        );
        command.Parameters.AddWithValue("company_id", legacy.CompanyId);
        command.Parameters.AddWithValue("user_id", legacy.RecipientUserId);
        command.Parameters.AddWithValue("phone_number", legacy.PhoneNumber);
        command.Parameters.AddWithValue("in_app_template_key", legacy.InAppTemplateKey);
        command.Parameters.AddWithValue("whatsapp_template_key", legacy.WhatsAppTemplateKey);
        command.Parameters.AddWithValue("in_app_intent_id", legacy.InAppIntentId);
        command.Parameters.AddWithValue("whatsapp_intent_id", legacy.WhatsAppIntentId);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        for (var column = 0; column < reader.FieldCount; column++)
        {
            Assert.True(reader.GetBoolean(column));
        }
    }

    private static async Task AssertPredecessorSchemaAsync(NpgsqlConnection administrator)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'notifications'
                      AND table_name = 'notification_recipient_contacts'
                      AND column_name = 'address'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'notifications'
                      AND table_name = 'notification_recipient_contacts'
                      AND column_name = 'phone_number'
                      AND is_nullable = 'NO'),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint AS constraint_row
                    WHERE constraint_row.conrelid =
                            'notifications.notification_recipient_contacts'::regclass
                      AND constraint_row.conname =
                            'notification_recipient_contacts_phone_number_check'
                      AND strpos(
                            pg_get_constraintdef(constraint_row.oid),
                            '^\+[1-9][0-9]{6,14}$') > 0),
                (
                    SELECT count(*)
                    FROM pg_constraint AS constraint_row
                    WHERE constraint_row.conname IN (
                        'notification_preferences_channel_check',
                        'notification_templates_channel_check',
                        'notification_intents_channel_check',
                        'notification_recipient_contacts_channel_check')
                ) = 4,
                NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint AS constraint_row
                    WHERE constraint_row.conname IN (
                        'notification_preferences_channel_check',
                        'notification_templates_channel_check',
                        'notification_intents_channel_check',
                        'notification_recipient_contacts_channel_check')
                      AND strpos(pg_get_constraintdef(constraint_row.oid), 'telegram') > 0),
                NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint AS constraint_row
                    WHERE constraint_row.conname IN (
                        'ck_notification_templates_telegram_text',
                        'ck_notification_recipient_contacts_channel_address'))
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        for (var column = 0; column < reader.FieldCount; column++)
        {
            Assert.True(reader.GetBoolean(column));
        }
    }

    private static async Task AssertPredecessorWritesAreRejectedAsync(
        NpgsqlConnection administrator,
        LegacyNotificationState legacy
    )
    {
        await AssertPostgresSqlStateAsync(
            PostgresErrorCodes.CheckViolation,
            () => ExecuteNonQueryAsync(
                administrator,
                """
                INSERT INTO notifications.notification_preferences (
                    company_id, user_id, channel, opted_in, updated_by_user_id)
                VALUES (@company_id, @user_id, 'telegram', true, @user_id)
                """,
                ("company_id", legacy.CompanyId),
                ("user_id", legacy.RecipientUserId)
            )
        );
        await AssertPostgresSqlStateAsync(
            PostgresErrorCodes.CheckViolation,
            () => ExecuteNonQueryAsync(
                administrator,
                """
                INSERT INTO notifications.notification_templates (
                    company_id, channel, template_key, is_approved)
                VALUES (@company_id, 'telegram', 'restored.telegram', true)
                """,
                ("company_id", legacy.CompanyId)
            )
        );
        await AssertPostgresSqlStateAsync(
            PostgresErrorCodes.CheckViolation,
            () => ExecuteNonQueryAsync(
                administrator,
                """
                INSERT INTO notifications.notification_intents (
                    id, company_id, recipient_user_id, event_key, channel, template_key,
                    status, correlation_id, created_by_user_id)
                VALUES (
                    @intent_id, @company_id, @user_id, 'restored-telegram-event', 'telegram',
                    'restored.telegram', 'pending_delivery', @correlation_id, @user_id)
                """,
                ("intent_id", Guid.NewGuid()),
                ("company_id", legacy.CompanyId),
                ("user_id", legacy.RecipientUserId),
                ("correlation_id", Guid.NewGuid())
            )
        );
        await AssertPostgresSqlStateAsync(
            PostgresErrorCodes.CheckViolation,
            () => ExecuteNonQueryAsync(
                administrator,
                """
                INSERT INTO notifications.notification_recipient_contacts (
                    company_id, user_id, channel, phone_number, is_validated, updated_by_user_id)
                VALUES (@company_id, @user_id, 'telegram', @phone_number, false, @user_id)
                """,
                ("company_id", legacy.CompanyId),
                ("user_id", legacy.RecipientUserId),
                ("phone_number", legacy.PhoneNumber)
            )
        );

        var additionalUserId = await SeedAdditionalMemberAsync(administrator, legacy);
        await AssertPostgresSqlStateAsync(
            PostgresErrorCodes.NotNullViolation,
            () => ExecuteNonQueryAsync(
                administrator,
                """
                INSERT INTO notifications.notification_recipient_contacts (
                    company_id, user_id, channel, phone_number, is_validated, updated_by_user_id)
                VALUES (@company_id, @user_id, 'whatsapp', NULL, false, @user_id)
                """,
                ("company_id", legacy.CompanyId),
                ("user_id", additionalUserId)
            )
        );
        await AssertPostgresSqlStateAsync(
            PostgresErrorCodes.CheckViolation,
            () => ExecuteNonQueryAsync(
                administrator,
                """
                INSERT INTO notifications.notification_recipient_contacts (
                    company_id, user_id, channel, phone_number, is_validated, updated_by_user_id)
                VALUES (@company_id, @user_id, 'whatsapp', '+0123456', false, @user_id)
                """,
                ("company_id", legacy.CompanyId),
                ("user_id", additionalUserId)
            )
        );
    }

    private static async Task AssertRestoredFunctionSecurityAsync(
        NpgsqlConnection administrator
    )
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                expected.signature,
                pg_get_userbyid(function_row.proowner),
                function_row.prosecdef,
                function_row.proconfig,
                has_function_privilege('vinx_runtime', function_row.oid, 'EXECUTE'),
                NOT has_function_privilege('vinx_migrator', function_row.oid, 'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(
                        function_row.proacl,
                        acldefault('f', function_row.proowner))) AS privilege
                    WHERE privilege.grantee = 0
                      AND privilege.privilege_type = 'EXECUTE'),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(COALESCE(
                        function_row.proacl,
                        acldefault('f', function_row.proowner))) AS privilege
                    WHERE privilege.privilege_type = 'EXECUTE'
                      AND privilege.grantee NOT IN (
                          function_row.proowner,
                          (SELECT oid FROM pg_roles WHERE rolname = 'vinx_runtime')))
            FROM (
                VALUES
                    ('notifications.enqueue_notification_intent(uuid,uuid,text,text,text,text)'),
                    ('notifications.set_channel_preference(uuid,uuid,text,boolean)'),
                    ('notifications.set_recipient_contact(uuid,uuid,text,text)')
            ) AS expected(signature)
            INNER JOIN pg_proc AS function_row
                ON function_row.oid = expected.signature::regprocedure
            ORDER BY expected.signature
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        var signatures = new List<string>();

        while (await reader.ReadAsync())
        {
            signatures.Add(reader.GetString(0));
            Assert.Equal("vinx_notifications_owner", reader.GetString(1));
            Assert.True(reader.GetBoolean(2));
            Assert.Equal(
                ["search_path=pg_catalog, notifications, pg_temp"],
                reader.GetFieldValue<string[]>(3)
            );
            Assert.True(reader.GetBoolean(4));
            Assert.True(reader.GetBoolean(5));
            Assert.True(reader.GetBoolean(6));
            Assert.True(reader.GetBoolean(7));
        }

        Assert.Equal(
            [
                "notifications.enqueue_notification_intent(uuid,uuid,text,text,text,text)",
                "notifications.set_channel_preference(uuid,uuid,text,boolean)",
                "notifications.set_recipient_contact(uuid,uuid,text,text)",
            ],
            [.. signatures.Order()]
        );
    }

    private static async Task AssertRestoredFunctionsAreCallableAsync(
        FoundationPostgresFixture fixture,
        LegacyNotificationState legacy
    )
    {
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var transaction = await runtime.BeginTransactionAsync();
        await SetRuntimeActorContextAsync(runtime, transaction, legacy.RecipientUserId);

        Assert.Equal(
            "created",
            await EnqueueIntentAsync(
                runtime,
                transaction,
                legacy.CompanyId,
                legacy.RecipientUserId,
                "restored-in-app-event",
                "in_app",
                legacy.InAppTemplateKey
            )
        );
        Assert.Equal(
            "created",
            await EnqueueIntentAsync(
                runtime,
                transaction,
                legacy.CompanyId,
                legacy.RecipientUserId,
                "restored-whatsapp-event",
                "whatsapp",
                legacy.WhatsAppTemplateKey
            )
        );
        await ExecuteNonQueryAsync(
            runtime,
            transaction,
            """
            SELECT notifications.set_channel_preference(
                @company_id, @user_id, 'whatsapp', true)
            """,
            ("company_id", legacy.CompanyId),
            ("user_id", legacy.RecipientUserId)
        );
        await ExecuteNonQueryAsync(
            runtime,
            transaction,
            """
            SELECT notifications.set_recipient_contact(
                @company_id, @user_id, 'whatsapp', @phone_number)
            """,
            ("company_id", legacy.CompanyId),
            ("user_id", legacy.RecipientUserId),
            ("phone_number", legacy.PhoneNumber)
        );
        await transaction.CommitAsync();
    }

    private static async Task<string> EnqueueIntentAsync(
        NpgsqlConnection runtime,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid recipientUserId,
        string eventKey,
        string channel,
        string templateKey
    )
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT status, delivery_id
            FROM notifications.enqueue_notification_intent(
                @company_id, @recipient_user_id, @event_key, @channel, @template_key, NULL)
            """,
            runtime,
            transaction
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("recipient_user_id", recipientUserId);
        command.Parameters.AddWithValue("event_key", eventKey);
        command.Parameters.AddWithValue("channel", channel);
        command.Parameters.AddWithValue("template_key", templateKey);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.False(reader.IsDBNull(1));
        return reader.GetString(0);
    }

    private static async Task AddActiveMembershipAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid userId
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await ExecuteNonQueryAsync(
            administrator,
            """
            INSERT INTO foundation.memberships (
                id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (
                gen_random_uuid(), @company_id, @user_id, @actor_user_id, @actor_user_id, @request_id,
                clock_timestamp(), clock_timestamp())
            """,
            ("company_id", companyId),
            ("user_id", userId),
            ("actor_user_id", factory.ActorUserId),
            ("request_id", Guid.NewGuid())
        );
    }

    private static async Task<Guid> SeedAdditionalMemberAsync(
        NpgsqlConnection administrator,
        LegacyNotificationState legacy
    )
    {
        var userId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        await ExecuteNonQueryAsync(
            administrator,
            """
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES (@user_id, @display_name, @email, clock_timestamp());
            INSERT INTO foundation.memberships (
                id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (
                gen_random_uuid(), @company_id, @user_id, @user_id, @user_id, @request_id,
                clock_timestamp(), clock_timestamp())
            """,
            ("user_id", userId),
            ("display_name", $"additional-recipient-{suffix}"),
            ("email", $"additional-recipient-{suffix}@example.test"),
            ("company_id", legacy.CompanyId),
            ("request_id", Guid.NewGuid())
        );
        return userId;
    }

    private static async Task<long> CountTelegramContactsAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid userId
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM notifications.notification_recipient_contacts
            WHERE company_id = @company_id
              AND user_id = @user_id
              AND channel = 'telegram'
            """,
            administrator
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", userId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task CallRuntimeFunctionAsync(
        FoundationApiFactory factory,
        Guid? actorUserId,
        string commandText,
        params (string Name, object? Value)[] parameters
    )
    {
        await using var runtime = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var transaction = await runtime.BeginTransactionAsync();
        if (actorUserId is not null)
        {
            await SetRuntimeActorContextAsync(runtime, transaction, actorUserId.Value);
        }

        await ExecuteNonQueryAsync(runtime, transaction, commandText, parameters);
        await transaction.CommitAsync();
    }

    private static async Task SetRuntimeActorContextAsync(
        NpgsqlConnection runtime,
        NpgsqlTransaction transaction,
        Guid actorUserId
    )
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                set_config('app.user_id', @actor_user_id::text, true),
                set_config('app.correlation_id', @correlation_id::text, true)
            """,
            runtime,
            transaction
        );
        command.Parameters.AddWithValue("actor_user_id", actorUserId);
        command.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        string commandText,
        params (string Name, object? Value)[] parameters
    ) => await ExecuteNonQueryAsync(connection, null, commandText, parameters);

    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string commandText,
        params (string Name, object? Value)[] parameters
    )
    {
        await using var command = new NpgsqlCommand(commandText, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertPostgresSqlStateAsync(
        string sqlState,
        Func<Task> action
    )
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(sqlState, exception.SqlState);
    }
}
