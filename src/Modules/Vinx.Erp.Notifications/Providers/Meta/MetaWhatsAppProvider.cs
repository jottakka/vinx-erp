using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Notifications.Providers.Meta;

/// <summary>
/// Provider do canal WhatsApp: busca o destino por fronteira estreita
/// (somente a tentativa em curso lê telefone e template) e envia pelo client
/// do Graph, sem transação aberta durante a chamada externa.
/// </summary>
public sealed class MetaWhatsAppProvider(
    FoundationDbContext database,
    MetaWhatsAppClient client,
    MetaWhatsAppOptions options,
    ILogger<MetaWhatsAppProvider> logger
) : INotificationProvider
{
    public string Channel => NotificationChannel.WhatsApp;

    public async Task<NotificationProviderResult> DeliverAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.DeliveryId == Guid.Empty || request.AttemptId == Guid.Empty)
        {
            return new NotificationProviderResult(NotificationProviderResultKind.Rejected);
        }

        var target = await ReadTargetAsync(request, cancellationToken);
        if (target is null)
        {
            logger.LogWarning(
                "Meta WhatsApp delivery {Operation} {Result} {Reason}",
                "meta_whatsapp_delivery",
                "rejected",
                "delivery_target_missing"
            );
            return new NotificationProviderResult(NotificationProviderResultKind.Rejected);
        }

        var message = MetaWhatsAppMapper.Map(
            target.PhoneNumber,
            target.MetaTemplateName!,
            target.MetaLanguage!,
            target.DeepLinkSelector,
            options.WebAppBaseUrl
        );
        return await client.SendAsync(message, cancellationToken);
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
            SELECT phone_number, meta_template_name, meta_language, deep_link_selector
            FROM notifications.get_notification_delivery_target(@delivery_id, @attempt_id)
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
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
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
        string PhoneNumber,
        string? MetaTemplateName,
        string? MetaLanguage,
        string? DeepLinkSelector
    );
}
