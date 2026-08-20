using Npgsql;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Semente e leitores para os testes da inbox in-app: empresas e memberships do
/// próprio ator autenticado, entregas conduzidas a estados específicos do
/// worker pelas funções reais e utilitários de auditoria/leitura de estado.
/// </summary>
internal static class NotificationInboxSeed
{
    public static async Task<Guid> CreateCompanyForActorAsync(
        FoundationApiFactory factory,
        bool whatsAppOptedIn = false
    )
    {
        var groupId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await ExecuteAsync(
            administrator,
            """
            INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@group_id, @name, @actor, @actor, @request_id, clock_timestamp(), clock_timestamp())
            """,
            ("group_id", groupId),
            ("name", $"inbox-group-{suffix}"),
            ("actor", factory.ActorUserId),
            ("request_id", Guid.NewGuid())
        );
        await ExecuteAsync(
            administrator,
            """
            INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@company_id, @group_id, @name, @actor, @actor, @request_id, clock_timestamp(), clock_timestamp())
            """,
            ("company_id", companyId),
            ("group_id", groupId),
            ("name", $"inbox-company-{suffix}"),
            ("actor", factory.ActorUserId),
            ("request_id", Guid.NewGuid())
        );
        await ExecuteAsync(
            administrator,
            """
            INSERT INTO foundation.memberships (id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (gen_random_uuid(), @company_id, @user_id, @actor, @actor, @request_id, clock_timestamp(), clock_timestamp())
            """,
            ("company_id", companyId),
            ("user_id", factory.ActorUserId),
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
            ("in_app_template", NotificationIntentSeed.InAppTemplateKey)
        );
        if (whatsAppOptedIn)
        {
            await ExecuteAsync(
                administrator,
                """
                INSERT INTO notifications.notification_templates (company_id, channel, template_key, is_approved)
                VALUES (@company_id, 'whatsapp', @whatsapp_template, true)
                """,
                ("company_id", companyId),
                ("whatsapp_template", NotificationIntentSeed.WhatsAppTemplateKey)
            );
            await ExecuteAsync(
                administrator,
                """
                INSERT INTO notifications.notification_preferences (company_id, user_id, channel, opted_in, updated_by_user_id)
                VALUES (@company_id, @user_id, 'whatsapp', true, @user_id)
                ON CONFLICT (company_id, user_id, channel) DO UPDATE
                    SET opted_in = true, updated_at = clock_timestamp()
                """,
                ("company_id", companyId),
                ("user_id", factory.ActorUserId)
            );
        }

        return companyId;
    }

    /// <summary>
    /// Enfileira um intent in_app para o ator autenticado e conduz a entrega
    /// até o estado desejado usando as funções reais do worker.
    /// </summary>
    public static async Task<Guid> CreateIntentInStateAsync(
        FoundationApiFactory factory,
        Guid companyId,
        string state,
        string? deepLinkSelector = null
    )
    {
        var result = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                companyId,
                factory.ActorUserId,
                NotificationIntentSeed.NewEventKey(),
                NotificationIntentSeed.InAppTemplateKey,
                DeepLinkSelector: deepLinkSelector
            )
        );
        var deliveryId = result.InApp!.DeliveryId!.Value;
        await DriveToStateAsync(factory, deliveryId, state);
        return deliveryId;
    }

    public static async Task<Guid> CreateDeliveredInAppIntentAsync(
        FoundationApiFactory factory,
        Guid companyId,
        string? deepLinkSelector = null
    ) => await CreateIntentInStateAsync(factory, companyId, "delivered", deepLinkSelector);

    public static async Task DriveToStateAsync(
        FoundationApiFactory factory,
        Guid deliveryId,
        string state
    )
    {
        if (state == "pending_delivery")
        {
            return;
        }

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        var correlationId = Guid.NewGuid();
        await ExecuteScalarAsync(
            administrator,
            "SELECT notifications.accept_notification_delivery(@delivery_id, @correlation_id)",
            ("delivery_id", deliveryId),
            ("correlation_id", correlationId)
        );
        if (state == "pending_provider")
        {
            return;
        }

        var attemptId = (Guid)
            await ExecuteScalarAsync(
                administrator,
                """
                SELECT attempt_id FROM notifications.claim_notification_delivery(@delivery_id)
                """,
                ("delivery_id", deliveryId)
            )!;
        if (state == "provider_in_flight")
        {
            return;
        }

        var outcome = state switch
        {
            "delivered" => "delivered",
            "provider_failed" => "failed",
            "provider_ambiguous" => "ambiguous",
            _ => throw new ArgumentException($"Unsupported seeded state: {state}", nameof(state)),
        };
        await ExecuteScalarAsync(
            administrator,
            "SELECT notifications.finalize_notification_delivery(@delivery_id, @attempt_id, @correlation_id, @outcome)",
            ("delivery_id", deliveryId),
            ("attempt_id", attemptId),
            ("correlation_id", correlationId),
            ("outcome", outcome)
        );
    }

    public static async Task RevokeMembershipAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid userId
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await ExecuteAsync(
            administrator,
            """
            UPDATE foundation.memberships
            SET deleted_at = clock_timestamp(), updated_at = clock_timestamp()
            WHERE company_id = @company_id AND user_id = @user_id AND deleted_at IS NULL
            """,
            ("company_id", companyId),
            ("user_id", userId)
        );
    }

    public static async Task SoftDeleteCompanyAsync(FoundationApiFactory factory, Guid companyId)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await ExecuteAsync(
            administrator,
            """
            UPDATE foundation.companies
            SET deleted_at = clock_timestamp(), updated_at = clock_timestamp()
            WHERE id = @company_id AND deleted_at IS NULL
            """,
            ("company_id", companyId)
        );
    }

    public static async Task<long> CountReadAuditsAsync(
        FoundationApiFactory factory,
        Guid deliveryId
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        return (long)
            await ExecuteScalarAsync(
                administrator,
                """
                SELECT count(*) FROM audit.audit_events
                WHERE action = 'notification.inbox.read' AND resource_id = @delivery_id
                """,
                ("delivery_id", deliveryId)
            )!;
    }

    public sealed record InboxReadState(string Status, DateTimeOffset? ReadAt, Guid? ReadByUserId);

    public static async Task<InboxReadState> ReadInboxStateAsync(
        FoundationApiFactory factory,
        Guid deliveryId
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT status, read_at, read_by_user_id
            FROM notifications.notification_intents
            WHERE id = @delivery_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("delivery_id", deliveryId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new InboxReadState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2)
        );
    }

    public static async Task<NpgsqlConnection> OpenRuntimeConnectionAsync(
        FoundationApiFactory factory
    )
    {
        var connection = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public static async Task<object> ExecuteScalarAsync(
        NpgsqlConnection connection,
        string commandText,
        params (string Name, object Value)[] parameters
    )
    {
        await using var command = new NpgsqlCommand(commandText, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The scalar query returned no value.");
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
