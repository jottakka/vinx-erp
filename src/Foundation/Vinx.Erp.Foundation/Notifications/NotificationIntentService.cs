using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vinx.Erp.Foundation.Notifications;

public sealed record NotificationIntentCommand(
    Guid CompanyId,
    Guid RecipientUserId,
    string EventKey,
    string InAppTemplateKey,
    string? WhatsAppTemplateKey = null,
    string? EmailTemplateKey = null,
    string? DeepLinkSelector = null,
    string? TelegramTemplateKey = null
);

public enum NotificationIntentEnqueueState
{
    Enqueued,
    Invalid,
    Forbidden,
    Unavailable,
}

public sealed record NotificationIntentOutcome(
    string Channel,
    Guid? DeliveryId,
    string Status,
    bool Created,
    string? SuppressionReason = null
);

public sealed record NotificationEnqueueResult(
    NotificationIntentEnqueueState State,
    NotificationIntentOutcome? WhatsApp = null,
    NotificationIntentOutcome? Email = null,
    NotificationIntentOutcome? InApp = null,
    NotificationIntentOutcome? Telegram = null
);

public enum NotificationPreferenceState
{
    Updated,
    Invalid,
    Forbidden,
    Unavailable,
}

public sealed record NotificationPreferenceResult(NotificationPreferenceState State);

/// <summary>
/// Cria as intenções de notificação e o evento Wolverine na mesma transação do
/// produtor; o alerta in-app é obrigatório e o opt-in de WhatsApp/e-mail/Telegram
/// é revalidado dentro da função de enfileiramento no momento da chamada para
/// cada canal externo habilitado.
/// </summary>
public class NotificationIntentService(
    FoundationDbContext database,
    IDbContextOutbox outbox,
    ILogger<NotificationIntentService> logger,
    string? enqueueFunctionName = null
)
{
    private const string Operation = "notification_intent";
    private const string Module = "foundation";

    /// <summary>
    /// Função SQL de enfileiramento. O runtime web usa a função genérica
    /// (notifications.enqueue_notification_intent); o worker usa uma função
    /// estreita worker-only (ex.: a de Demandas) com a MESMA assinatura — a
    /// função base é revogada do vinx_worker.
    /// </summary>
    private readonly string enqueueFunction =
        enqueueFunctionName ?? "notifications.enqueue_notification_intent";

    public async Task<NotificationEnqueueResult> EnqueueAsync(
        ActorContext actor,
        NotificationIntentCommand command,
        CancellationToken cancellationToken
    )
    {
        if (!TryNormalize(command, out var normalized))
        {
            return new NotificationEnqueueResult(NotificationIntentEnqueueState.Invalid);
        }

        var commitAttempted = false;
        NotificationIntentOutcome? whatsApp = null;
        NotificationIntentOutcome? email = null;
        NotificationIntentOutcome? telegram = null;
        NotificationIntentOutcome? inApp = null;
        var createdDeliveries = new List<Guid>();
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
            if (normalized.WhatsAppTemplateKey is not null)
            {
                whatsApp = await EnqueueChannelAsync(
                    transaction,
                    normalized,
                    NotificationChannel.WhatsApp,
                    normalized.WhatsAppTemplateKey,
                    createdDeliveries,
                    cancellationToken
                );
            }

            if (normalized.EmailTemplateKey is not null)
            {
                email = await EnqueueChannelAsync(
                    transaction,
                    normalized,
                    NotificationChannel.Email,
                    normalized.EmailTemplateKey,
                    createdDeliveries,
                    cancellationToken
                );
            }

            if (normalized.TelegramTemplateKey is not null)
            {
                telegram = await EnqueueChannelAsync(
                    transaction,
                    normalized,
                    NotificationChannel.Telegram,
                    normalized.TelegramTemplateKey,
                    createdDeliveries,
                    cancellationToken
                );
            }

            inApp = await EnqueueChannelAsync(
                transaction,
                normalized,
                NotificationChannel.InApp,
                normalized.InAppTemplateKey,
                createdDeliveries,
                cancellationToken
            );

            if (createdDeliveries.Count > 0)
            {
                outbox.Enroll(database);
                foreach (var deliveryId in createdDeliveries)
                {
                    await outbox.PublishAsync(
                        new NotificationDeliveryRequested(deliveryId),
                        new DeliveryOptions { CorrelationId = actor.CorrelationId.ToString("D") }
                    );
                }
            }

            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
            when (!commitAttempted && IsExpectedAccessDenial(exception))
        {
            return new NotificationEnqueueResult(NotificationIntentEnqueueState.Forbidden);
        }
        catch (PostgresException exception)
            when (!commitAttempted && IsExpectedInvalidIntent(exception))
        {
            return new NotificationEnqueueResult(NotificationIntentEnqueueState.Invalid);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Notification intent enqueue {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "unavailable",
                commitAttempted ? "outbox_commit_outcome_unknown" : "database_or_audit_unavailable"
            );
            return new NotificationEnqueueResult(NotificationIntentEnqueueState.Unavailable);
        }

        if (createdDeliveries.Count > 0)
        {
            try
            {
                await outbox.FlushOutgoingMessagesAsync();
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Notification intent enqueue {Operation} {Module} {Result} {Reason}",
                    Operation,
                    Module,
                    "enqueued",
                    "outbox_flush_deferred"
                );
            }
        }

        return new NotificationEnqueueResult(
            NotificationIntentEnqueueState.Enqueued,
            whatsApp,
            email,
            inApp,
            telegram
        );
    }

    public async Task<NotificationPreferenceResult> SetChannelPreferenceAsync(
        ActorContext actor,
        Guid companyId,
        string channel,
        bool optedIn,
        CancellationToken cancellationToken
    )
    {
        if (
            companyId == Guid.Empty
            || channel
                is not (
                    NotificationChannel.WhatsApp
                    or NotificationChannel.Email
                    or NotificationChannel.Telegram
                )
        )
        {
            return new NotificationPreferenceResult(NotificationPreferenceState.Invalid);
        }

        var commitAttempted = false;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
            var connection = database.Database.GetDbConnection();
            await using var sql = connection.CreateCommand();
            sql.Transaction = transaction.GetDbTransaction();
            sql.CommandText =
                "SELECT notifications.set_channel_preference(@company_id, @user_id, @channel, @opted_in)";
            AddParameter(sql, "company_id", companyId);
            AddParameter(sql, "user_id", actor.UserId);
            AddParameter(sql, "channel", channel);
            AddParameter(sql, "opted_in", optedIn);
            await sql.ExecuteNonQueryAsync(cancellationToken);
            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
            when (!commitAttempted && IsExpectedAccessDenial(exception))
        {
            return new NotificationPreferenceResult(NotificationPreferenceState.Forbidden);
        }
        catch (PostgresException exception)
            when (!commitAttempted && IsExpectedInvalidIntent(exception))
        {
            return new NotificationPreferenceResult(NotificationPreferenceState.Invalid);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Notification preference change {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "unavailable",
                commitAttempted ? "commit_outcome_unknown" : "database_or_audit_unavailable"
            );
            return new NotificationPreferenceResult(NotificationPreferenceState.Unavailable);
        }

        return new NotificationPreferenceResult(NotificationPreferenceState.Updated);
    }

    /// <summary>
    /// Chama a função de um canal e classifica o resultado; somente entregas
    /// novas alimentam o outbox — replay não publica segunda mensagem.
    /// </summary>
    private async Task<NotificationIntentOutcome> EnqueueChannelAsync(
        IDbContextTransaction transaction,
        NormalizedCommand command,
        string channel,
        string templateKey,
        List<Guid> createdDeliveries,
        CancellationToken cancellationToken
    )
    {
        var connection = database.Database.GetDbConnection();
        await using var sql = connection.CreateCommand();
        sql.Transaction = transaction.GetDbTransaction();
        sql.CommandText = $"""
            SELECT status, delivery_id
            FROM {enqueueFunction}(
                @company_id, @recipient_user_id, @event_key, @channel, @template_key, @deep_link_selector)
            """;
        AddParameter(sql, "company_id", command.CompanyId);
        AddParameter(sql, "recipient_user_id", command.RecipientUserId);
        AddParameter(sql, "event_key", command.EventKey);
        AddParameter(sql, "channel", channel);
        AddParameter(sql, "template_key", templateKey);
        AddParameter(sql, "deep_link_selector", (object?)command.DeepLinkSelector ?? DBNull.Value);
        await using var reader = await sql.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Notification intent enqueuing did not return a result."
            );
        }

        var status = reader.GetString(0);
        Guid? deliveryId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        if (status == "created")
        {
            createdDeliveries.Add(deliveryId!.Value);
            return new NotificationIntentOutcome(channel, deliveryId, "pending_delivery", true);
        }

        if (status == "replay")
        {
            return new NotificationIntentOutcome(channel, deliveryId, "pending_delivery", false);
        }

        if (status == "suppressed_channel_opt_out")
        {
            return new NotificationIntentOutcome(
                channel,
                null,
                "suppressed_channel_opt_out",
                false,
                "channel_opt_out"
            );
        }

        throw new InvalidOperationException(
            "Notification intent enqueuing returned an unknown state."
        );
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool IsExpectedAccessDenial(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.InsufficientPrivilege
        && exception.MessageText
            is "notification actor is required"
                or "recipient is not a member of the company"
                or "notification preference changes are self-service";

    private static bool IsExpectedInvalidIntent(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.InvalidParameterValue
        && exception.MessageText
            is "notification intent parameters are invalid"
                or "notification template is not approved"
                or "notification preference parameters are invalid";

    private static bool TryNormalize(
        NotificationIntentCommand command,
        out NormalizedCommand normalized
    )
    {
        normalized = default!;
        if (command.CompanyId == Guid.Empty || command.RecipientUserId == Guid.Empty)
        {
            return false;
        }

        var eventKey = command.EventKey?.Trim();
        var inAppTemplateKey = command.InAppTemplateKey?.Trim();
        var whatsAppTemplateKey = command.WhatsAppTemplateKey?.Trim();
        var emailTemplateKey = command.EmailTemplateKey?.Trim();
        var deepLinkSelector = command.DeepLinkSelector?.Trim();
        var telegramTemplateKey = command.TelegramTemplateKey?.Trim();
        if (
            string.IsNullOrWhiteSpace(eventKey)
            || eventKey.Length > 200
            || string.IsNullOrWhiteSpace(inAppTemplateKey)
            || inAppTemplateKey.Length > 100
            || whatsAppTemplateKey is { Length: 0 }
            || whatsAppTemplateKey?.Length > 100
            || emailTemplateKey is { Length: 0 }
            || emailTemplateKey?.Length > 100
            || telegramTemplateKey is { Length: 0 }
            || telegramTemplateKey?.Length > 100
            || (
                deepLinkSelector is not null
                && (deepLinkSelector.Length > 200 || !deepLinkSelector.StartsWith('/'))
            )
        )
        {
            return false;
        }

        normalized = new NormalizedCommand(
            command.CompanyId,
            command.RecipientUserId,
            eventKey,
            inAppTemplateKey,
            whatsAppTemplateKey is { Length: > 0 } ? whatsAppTemplateKey : null,
            emailTemplateKey is { Length: > 0 } ? emailTemplateKey : null,
            deepLinkSelector is { Length: > 0 } ? deepLinkSelector : null,
            telegramTemplateKey is { Length: > 0 } ? telegramTemplateKey : null
        );
        return true;
    }

    private sealed record NormalizedCommand(
        Guid CompanyId,
        Guid RecipientUserId,
        string EventKey,
        string InAppTemplateKey,
        string? WhatsAppTemplateKey,
        string? EmailTemplateKey,
        string? DeepLinkSelector,
        string? TelegramTemplateKey
    );
}
