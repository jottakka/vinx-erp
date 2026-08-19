using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

internal sealed record NotificationCompanySeed(
    Guid CompanyId,
    Guid RecipientUserId,
    string InAppTemplateKey,
    string WhatsAppTemplateKey
);

internal sealed record NotificationIntentRow(
    Guid Id,
    Guid CompanyId,
    Guid RecipientUserId,
    string Channel,
    string EventKey,
    string Status,
    int AttemptCount,
    DateTimeOffset UpdatedAt
);

internal sealed record NotificationQueueMessage(byte[] Body, string Metadata);

/// <summary>
/// Semente e leitores para os testes de intenção de notificação; todas as
/// escritas de arranque usam a conexão de administrador (superusuário) e as
/// ações sob teste passam pelo serviço real do host.
/// </summary>
internal static class NotificationIntentSeed
{
    public const string InAppTemplateKey = "alert.in_app";
    public const string WhatsAppTemplateKey = "alert.whatsapp";

    public static string NewEventKey() => $"event-{Guid.NewGuid():N}";

    public static async Task<NotificationCompanySeed> CreateCompanyWithRecipientAsync(
        FoundationApiFactory factory,
        bool whatsAppOptedIn = false,
        bool whatsAppTemplateApproved = true
    )
    {
        var groupId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await ExecuteAsync(
            administrator,
            """
            INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@group_id, @group_name, @actor, @actor, @request_id, clock_timestamp(), clock_timestamp())
            """,
            ("group_id", groupId),
            ("group_name", $"group-{suffix}"),
            ("actor", factory.ActorUserId),
            ("request_id", Guid.NewGuid())
        );
        await ExecuteAsync(
            administrator,
            """
            INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@company_id, @group_id, @company_name, @actor, @actor, @request_id, clock_timestamp(), clock_timestamp())
            """,
            ("company_id", companyId),
            ("group_id", groupId),
            ("company_name", $"company-{suffix}"),
            ("actor", factory.ActorUserId),
            ("request_id", Guid.NewGuid())
        );
        await ExecuteAsync(
            administrator,
            """
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES (@recipient_id, @display_name, @email, clock_timestamp())
            """,
            ("recipient_id", recipientId),
            ("display_name", $"recipient-{suffix}"),
            ("email", $"recipient-{suffix}@example.test")
        );
        await ExecuteAsync(
            administrator,
            """
            INSERT INTO foundation.memberships (id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (gen_random_uuid(), @company_id, @recipient_id, @actor, @actor, @request_id, clock_timestamp(), clock_timestamp())
            """,
            ("company_id", companyId),
            ("recipient_id", recipientId),
            ("actor", factory.ActorUserId),
            ("request_id", Guid.NewGuid())
        );
        await ExecuteAsync(
            administrator,
            """
            INSERT INTO notifications.notification_templates (company_id, channel, template_key, is_approved)
            VALUES (@company_id, 'in_app', @in_app_template, true)
            """,
            ("company_id", companyId),
            ("in_app_template", InAppTemplateKey)
        );
        await ExecuteAsync(
            administrator,
            """
            INSERT INTO notifications.notification_templates (company_id, channel, template_key, is_approved)
            VALUES (@company_id, 'whatsapp', @whatsapp_template, @whatsapp_approved)
            """,
            ("company_id", companyId),
            ("whatsapp_template", WhatsAppTemplateKey),
            ("whatsapp_approved", whatsAppTemplateApproved)
        );

        if (whatsAppOptedIn)
        {
            await SeedPreferenceAsync(factory, companyId, recipientId, optedIn: true);
        }

        return new NotificationCompanySeed(
            companyId,
            recipientId,
            InAppTemplateKey,
            WhatsAppTemplateKey
        );
    }

    public static async Task SeedPreferenceAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid userId,
        bool optedIn
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO notifications.notification_preferences (company_id, user_id, channel, opted_in, updated_by_user_id)
            VALUES (@company_id, @user_id, 'whatsapp', @opted_in, @user_id)
            ON CONFLICT (company_id, user_id, channel) DO UPDATE
                SET opted_in = EXCLUDED.opted_in,
                    updated_at = clock_timestamp(),
                    updated_by_user_id = EXCLUDED.updated_by_user_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("opted_in", optedIn);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<NotificationEnqueueResult> EnqueueAsync(
        FoundationApiFactory factory,
        NotificationIntentCommand command
    )
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationIntentService>();
        var actor = new ActorContext(factory.ActorUserId, factory.CorrelationId);
        return await service.EnqueueAsync(actor, command, CancellationToken.None);
    }

    public static async Task<NotificationPreferenceResult> SetPreferenceViaServiceAsync(
        FoundationApiFactory factory,
        Guid actorUserId,
        Guid companyId,
        bool optedIn
    )
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationIntentService>();
        var actor = new ActorContext(actorUserId, Guid.NewGuid());
        return await service.SetChannelPreferenceAsync(
            actor,
            companyId,
            NotificationChannel.WhatsApp,
            optedIn,
            CancellationToken.None
        );
    }

    public static async Task<IReadOnlyList<NotificationIntentRow>> ReadIntentsAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT id, company_id, recipient_user_id, channel, event_key, status, attempt_count, updated_at
            FROM notifications.notification_intents
            ORDER BY created_at, id
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<NotificationIntentRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(
                new NotificationIntentRow(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetFieldValue<DateTimeOffset>(7)
                )
            );
        }

        return rows;
    }

    public static async Task<IReadOnlyList<NotificationQueueMessage>> ReadNotificationMessagesAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        Assert.True(
            await NotificationQueueExistsAsync(administrator),
            "Wolverine notification-delivery storage must be provisioned by deploy before message assertions; tests never create it."
        );

        await using var command = new NpgsqlCommand(
            "SELECT body, row_to_json(queue)::text FROM vinx_wolverine_queues.wolverine_queue_notification_delivery AS queue",
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        var messages = new List<NotificationQueueMessage>();
        while (await reader.ReadAsync())
        {
            messages.Add(
                new NotificationQueueMessage(
                    reader.GetFieldValue<byte[]>(0),
                    reader.GetString(1)
                )
            );
        }

        return messages;
    }

    public static async Task<bool> NotificationQueueExistsAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        return await NotificationQueueExistsAsync(administrator);
    }

    public static async Task<long> CountAuditEventsAsync(
        FoundationApiFactory factory,
        string action
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM audit.audit_events WHERE action = @action",
            administrator
        );
        command.Parameters.AddWithValue("action", action);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> NotificationQueueExistsAsync(
        NpgsqlConnection administrator
    )
    {
        await using var exists = new NpgsqlCommand(
            "SELECT to_regclass('vinx_wolverine_queues.wolverine_queue_notification_delivery')::text",
            administrator
        );
        var tableName = await exists.ExecuteScalarAsync();
        return tableName is string;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection administrator,
        string commandText,
        params (string Name, object Value)[] parameters
    )
    {
        await using var command = new NpgsqlCommand(commandText, administrator);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
