using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Notifications;
using Vinx.Erp.Notifications.Providers.Meta;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vinx.Erp.ApiHost.Notifications;

/// <summary>Imagem inbound extraída do webhook: ids do canal/provider e legenda opcional.</summary>
public sealed record InboundWhatsAppImage(
    string MessageId,
    string PhoneNumberId,
    string WaId,
    string MediaId,
    string? Caption
);

/// <summary>
/// Documento inbound extraído do webhook: ids do canal/provider e legenda
/// opcional. Nenhum byte é baixado nesta fatia (#272 PR1).
/// </summary>
public sealed record InboundWhatsAppDocument(
    string MessageId,
    string PhoneNumberId,
    string WaId,
    string MediaId,
    string? Caption
);

/// <summary>Texto inbound extraído do webhook após a validação da assinatura.</summary>
public sealed record InboundWhatsAppText(
    string MessageId,
    string PhoneNumberId,
    string WaId,
    string Body
);

/// <summary>
/// Admite mensagens inbound com resolução de identidade
/// fechada e durável: deduplica por (canal, message_id), grava o resultado
/// da resolução na mesma transação e somente publica o evento Wolverine
/// quando a mensagem é admitida com exatamente um usuário, uma empresa e
/// uma membership ativa. Replay do webhook não gera novo processamento.
/// </summary>
public sealed class InboundWhatsAppIngressService(
    FoundationDbContext database,
    IDbContextOutbox outbox,
    IInboundActorResolver actorResolver,
    MetaWhatsAppOptions options,
    ILogger<InboundWhatsAppIngressService> logger
)
{
    private const string Operation = "whatsapp_inbound_ingress";
    private const string Module = "notifications";
    private static readonly TimeSpan CorrelationWindow = TimeSpan.FromSeconds(30);

    public Task<bool> RecordAsync(InboundWhatsAppImage image, CancellationToken cancellationToken) =>
        RecordAsync(
            image.MessageId,
            image.PhoneNumberId,
            image.WaId,
            "image",
            image.MediaId,
            image.Caption,
            null,
            cancellationToken
        );

    public Task<bool> RecordAsync(
        InboundWhatsAppDocument document,
        CancellationToken cancellationToken
    ) =>
        RecordAsync(
            document.MessageId,
            document.PhoneNumberId,
            document.WaId,
            "document",
            document.MediaId,
            document.Caption,
            null,
            cancellationToken
        );

    public Task<bool> RecordAsync(InboundWhatsAppText text, CancellationToken cancellationToken) =>
        RecordAsync(
            text.MessageId,
            text.PhoneNumberId,
            text.WaId,
            "text",
            null,
            null,
            text.Body,
            cancellationToken
        );

    private async Task<bool> RecordAsync(
        string messageId,
        string phoneNumberId,
        string waId,
        string messageType,
        string? mediaId,
        string? caption,
        string? textBody,
        CancellationToken cancellationToken
    )
    {
        if (!options.IsKnownInboundChannel(phoneNumberId))
        {
            // Canal desconhecido: a mensagem não avança (chave de idempotência e
            // identidade dependem do canal estável configurado).
            logger.LogWarning(
                "Inbound ingress {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "ignored",
                string.IsNullOrWhiteSpace(options.PhoneNumberId) ? "channel_unconfigured" : "channel_unknown"
            );
            return false;
        }

        var inboundId = Guid.NewGuid();
        var commitAttempted = false;
        var created = false;
        string? outboundKind = null;
        var scheduleDelay = false;
        var committedByOutbox = false;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            var resolution = await actorResolver.ResolveAsync(
                new InboundChannelAddress(phoneNumberId.Trim(), waId),
                cancellationToken
            );

            var connection = database.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = messageType == "text"
                ? "SELECT notifications.admit_inbound_whatsapp_text("
                    + "@inbound_id, @phone_number_id, @message_id, @wa_id, @text_body, "
                    + "@resolution_state, @user_id, @company_id, @membership_id)"
                : "SELECT notifications.admit_inbound_whatsapp_media("
                    + "@inbound_id, @phone_number_id, @message_id, @wa_id, @message_type, @media_id, @caption, "
                    + "@resolution_state, @user_id, @company_id, @membership_id)";
            AddParameter(command, "inbound_id", inboundId);
            AddParameter(command, "phone_number_id", phoneNumberId.Trim());
            AddParameter(command, "message_id", messageId);
            AddParameter(command, "wa_id", waId);
            if (messageType == "text")
            {
                AddParameter(command, "text_body", (object?)textBody ?? DBNull.Value);
            }
            else
            {
                AddParameter(command, "message_type", messageType);
                AddParameter(command, "media_id", (object?)mediaId ?? DBNull.Value);
                AddParameter(command, "caption", caption is null ? DBNull.Value : caption);
            }
            AddParameter(command, "resolution_state", resolution.SerializeState());
            AddParameter(command, "user_id", resolution.UserId.HasValue ? resolution.UserId.Value : DBNull.Value);
            AddParameter(
                command,
                "company_id",
                resolution.CompanyId.HasValue ? resolution.CompanyId.Value : DBNull.Value
            );
            AddParameter(
                command,
                "membership_id",
                resolution.MembershipId.HasValue ? resolution.MembershipId.Value : DBNull.Value
            );
            var admission = (string?)await command.ExecuteScalarAsync(cancellationToken);
            created = string.Equals(admission, "created", StringComparison.Ordinal);
            if (!created && !string.Equals(admission, "replay", StringComparison.Ordinal))
            {
                // messageId reaproveitado com payload incompatível: conflito
                // seguro, sem duplicar o inbound nem avançar o processamento.
                logger.LogWarning(
                    "Inbound ingress {Operation} {Module} {Result} {Reason}",
                    Operation,
                    Module,
                    "conflict",
                    "message_id_conflict"
                );
            }

            string? correlation = null;
            if (
                created
                && resolution.IsAdmitted
                && (messageType == "text" || (messageType == "image" && caption is null))
            )
            {
                await using var correlationCommand = connection.CreateCommand();
                correlationCommand.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
                correlationCommand.CommandText =
                    "SELECT notifications.correlate_inbound_whatsapp_text_image(@inbound_id)";
                AddParameter(correlationCommand, "inbound_id", inboundId);
                correlation = (string?)await correlationCommand.ExecuteScalarAsync(cancellationToken);
            }

            if (created && resolution.IsAdmitted)
            {
                if (messageType == "text")
                {
                    outboundKind = "text_expiry";
                    scheduleDelay = true;
                }
                else if (messageType == "image")
                {
                    if (caption is not null || string.Equals(correlation, "correlated", StringComparison.Ordinal))
                    {
                        outboundKind = "image";
                    }
                    else if (string.Equals(correlation, "no_match", StringComparison.Ordinal))
                    {
                        outboundKind = "image";
                        scheduleDelay = true;
                    }
                }
                else if (messageType == "document")
                {
                    outboundKind = "document";
                }
            }

            if (outboundKind is not null)
            {
                outbox.Enroll(database);
                var delivery = new DeliveryOptions
                {
                    CorrelationId = Guid.NewGuid().ToString("D"),
                };
                if (scheduleDelay)
                {
                    delivery.ScheduleDelay = CorrelationWindow;
                }

                switch (outboundKind)
                {
                    case "image":
                        await outbox.PublishAsync(
                            new InboundWhatsAppImageReceived(inboundId),
                            delivery
                        );
                        break;
                    case "text_expiry":
                        await outbox.PublishAsync(
                            new InboundWhatsAppTextCorrelationExpired(inboundId),
                            delivery
                        );
                        break;
                    case "document":
                        await outbox.PublishAsync(
                            new InboundWhatsAppDocumentReceived(inboundId),
                            delivery
                        );
                        break;
                }

                commitAttempted = true;
                await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
                committedByOutbox = true;
            }

            if (!committedByOutbox)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (PostgresException exception)
            when (!commitAttempted && exception.SqlState == PostgresErrorCodes.InvalidParameterValue)
        {
            // Payload aceito pelo provider mas inválido para o contrato interno:
            // ignora sem quebrar o webhook (as demais mensagens do POST seguem).
            logger.LogWarning(
                "Inbound ingress {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "ignored",
                "invalid_parameters"
            );
            return false;
        }

        return created;
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
