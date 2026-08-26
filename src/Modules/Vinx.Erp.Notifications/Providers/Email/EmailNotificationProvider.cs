using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Notifications.Providers.Email;

/// <summary>
/// Provider do canal e-mail: busca o destino por fronteira estreita (somente a
/// tentativa em curso lê endereço, assunto e corpo aprovados) e envia por SMTP,
/// sem transação aberta durante a chamada externa. O endereço vem do cadastro de
/// identidade (foundation.users.email), sem preferência de contato separada.
/// </summary>
public sealed class EmailNotificationProvider(
    FoundationDbContext database,
    EmailProviderOptions options,
    ILogger<EmailNotificationProvider> logger
) : INotificationProvider
{
    public string Channel => NotificationChannel.Email;

    public async Task<NotificationProviderResult> DeliverAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.DeliveryId == Guid.Empty || request.AttemptId == Guid.Empty)
        {
            return new NotificationProviderResult(NotificationProviderResultKind.Rejected);
        }

        if (!options.OutboundIsConfigured)
        {
            logger.LogWarning(
                "SMTP email delivery {Operation} {Result} {Reason}",
                "smtp_email_delivery",
                "rejected",
                "provider_unconfigured"
            );
            return new NotificationProviderResult(NotificationProviderResultKind.Rejected);
        }

        var target = await ReadTargetAsync(request, cancellationToken);
        if (target is null)
        {
            logger.LogWarning(
                "SMTP email delivery {Operation} {Result} {Reason}",
                "smtp_email_delivery",
                "rejected",
                "delivery_target_missing"
            );
            return new NotificationProviderResult(NotificationProviderResultKind.Rejected);
        }

        var client = new SmtpEmailClient(options, logger);
        return await client.SendAsync(
            new EmailMessage(target.Email, target.Subject, target.BodyHtml),
            cancellationToken
        );
    }

    private async Task<DeliveryTarget?> ReadTargetAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        var connection = database.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            SELECT email, subject, body_html, deep_link_selector
            FROM notifications.get_notification_email_delivery_target(@delivery_id, @attempt_id)
            """;
        AddParameter(command, "delivery_id", request.DeliveryId);
        AddParameter(command, "attempt_id", request.AttemptId);

        DeliveryTarget? target = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0))
            {
                target = new DeliveryTarget(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)
                );
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return target;
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record DeliveryTarget(
        string Email,
        string Subject,
        string BodyHtml,
        string? DeepLinkSelector
    );
}
