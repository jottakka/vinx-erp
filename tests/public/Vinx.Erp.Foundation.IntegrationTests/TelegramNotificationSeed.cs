using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;

namespace Vinx.Erp.Foundation.IntegrationTests;

internal sealed record TelegramNotificationCompanySeed(
    Guid CompanyId,
    Guid RecipientUserId,
    string InAppTemplateKey,
    string TelegramTemplateKey,
    string TelegramText,
    string TelegramAddress
);

internal sealed record TelegramContactRow(
    string? Address,
    string? PhoneNumber,
    bool IsValidated
);

/// <summary>
/// Semente isolada para o contrato Telegram; a identidade e os dados genéricos
/// de empresa são criados pela semente de intenção existente.
/// </summary>
internal static class TelegramNotificationSeed
{
    public const string TelegramTemplateKey = "alert.telegram";
    public const string TelegramText = "Alerta Telegram aprovado";
    public const string TelegramAddress = "123456789";

    public static async Task<TelegramNotificationCompanySeed> CreateCompanyWithRecipientAsync(
        FoundationApiFactory factory,
        bool? telegramOptedIn = null,
        bool telegramTemplateApproved = true,
        string? telegramText = TelegramText,
        string? telegramAddress = TelegramAddress,
        bool telegramContactValidated = false
    )
    {
        var generic = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await ExecuteAsync(
            administrator,
            """
            INSERT INTO notifications.notification_templates
                (company_id, channel, template_key, is_approved, telegram_text)
            VALUES (@company_id, 'telegram', @template_key, @is_approved, @telegram_text)
            """,
            ("company_id", generic.CompanyId),
            ("template_key", TelegramTemplateKey),
            ("is_approved", telegramTemplateApproved),
            ("telegram_text", telegramText)
        );

        if (telegramOptedIn is not null)
        {
            await ExecuteAsync(
                administrator,
                """
                INSERT INTO notifications.notification_preferences
                    (company_id, user_id, channel, opted_in, updated_by_user_id)
                VALUES (@company_id, @user_id, 'telegram', @opted_in, @user_id)
                """,
                ("company_id", generic.CompanyId),
                ("user_id", generic.RecipientUserId),
                ("opted_in", telegramOptedIn.Value)
            );
        }

        if (telegramAddress is not null)
        {
            await ExecuteAsync(
                administrator,
                """
                INSERT INTO notifications.notification_recipient_contacts
                    (company_id, user_id, channel, address, is_validated, updated_by_user_id)
                VALUES (@company_id, @user_id, 'telegram', @address, @is_validated, @user_id)
                """,
                ("company_id", generic.CompanyId),
                ("user_id", generic.RecipientUserId),
                ("address", telegramAddress),
                ("is_validated", telegramContactValidated)
            );
        }

        return new TelegramNotificationCompanySeed(
            generic.CompanyId,
            generic.RecipientUserId,
            generic.InAppTemplateKey,
            TelegramTemplateKey,
            telegramText ?? string.Empty,
            telegramAddress ?? string.Empty
        );
    }

    public static async Task<NotificationPreferenceResult> SetPreferenceViaServiceAsync(
        FoundationApiFactory factory,
        Guid userId,
        Guid companyId,
        bool optedIn
    )
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationIntentService>();
        return await service.SetChannelPreferenceAsync(
            new ActorContext(userId, Guid.NewGuid()),
            companyId,
            NotificationChannel.Telegram,
            optedIn,
            CancellationToken.None
        );
    }

    public static async Task SetContactAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid userId,
        string address
    )
    {
        await using var runtime = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var transaction = await runtime.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT set_config('app.user_id', @user_id::text, true),
                set_config('app.correlation_id', @correlation_id::text, true),
                notifications.set_recipient_contact(@company_id, @user_id, 'telegram', @address)
            """,
            runtime,
            transaction
        );
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
        command.Parameters.AddWithValue("address", address);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public static async Task<TelegramContactRow> ReadContactAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid userId
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT address, phone_number, is_validated
            FROM notifications.notification_recipient_contacts
            WHERE company_id = @company_id AND user_id = @user_id AND channel = 'telegram'
            """,
            administrator
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("The Telegram contact was not seeded.");
        }

        return new TelegramContactRow(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetBoolean(2)
        );
    }

    public static async Task RevokeMembershipAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid userId
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE foundation.memberships SET deleted_at = clock_timestamp() WHERE company_id = @company_id AND user_id = @user_id",
            administrator
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task RevokeOutboxInsertAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE INSERT ON vinx_wolverine.wolverine_outgoing_envelopes FROM vinx_runtime;",
            administrator
        );
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> CountTelegramPreferencesAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid userId
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM notifications.notification_preferences WHERE company_id = @company_id AND user_id = @user_id AND channel = 'telegram'",
            administrator
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", userId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string commandText,
        params (string Name, object? Value)[] parameters
    )
    {
        await using var command = new NpgsqlCommand(commandText, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
