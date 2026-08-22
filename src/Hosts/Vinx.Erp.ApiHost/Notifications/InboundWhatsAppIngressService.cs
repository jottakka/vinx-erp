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

/// <summary>
/// Admite mídia inbound (imagem/documento) com resolução de identidade
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

    public Task<bool> RecordAsync(InboundWhatsAppImage image, CancellationToken cancellationToken) =>
        RecordMediaAsync(
            image.MessageId,
            image.PhoneNumberId,
            image.WaId,
            "image",
            image.MediaId,
            image.Caption,
            cancellationToken
        );

    public Task<bool> RecordAsync(
        InboundWhatsAppDocument document,
        CancellationToken cancellationToken
    ) =>
        RecordMediaAsync(
            document.MessageId,
            document.PhoneNumberId,
            document.WaId,
            "document",
            document.MediaId,
            document.Caption,
            cancellationToken
        );

    private async Task<bool> RecordMediaAsync(
        string messageId,
        string phoneNumberId,
        string waId,
        string messageType,
        string mediaId,
        string? caption,
        CancellationToken cancellationToken
    )
    {
        if (!options.IsKnownInboundChannel(phoneNumberId))
        {
            // Canal desconhecido: a mídia não avança (chave de idempotência e
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
        var shouldPublish = false;
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
            command.CommandText =
                "SELECT notifications.admit_inbound_whatsapp_media("
                + "@inbound_id, @phone_number_id, @message_id, @wa_id, @message_type, @media_id, @caption, "
                + "@resolution_state, @user_id, @company_id, @membership_id)";
            AddParameter(command, "inbound_id", inboundId);
            AddParameter(command, "phone_number_id", phoneNumberId.Trim());
            AddParameter(command, "message_id", messageId);
            AddParameter(command, "wa_id", waId);
            AddParameter(command, "message_type", messageType);
            AddParameter(command, "media_id", mediaId);
            AddParameter(command, "caption", caption is null ? DBNull.Value : caption);
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
            shouldPublish = created && resolution.IsAdmitted;
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

            if (shouldPublish)
            {
                outbox.Enroll(database);
                // Imagem admitida segue ao subsete #140 (OCR/resposta);
                // documento admitido segue à fila de quarentena do #272 PR2.
                object message = messageType == "image"
                    ? new InboundWhatsAppImageReceived(inboundId)
                    : new InboundWhatsAppDocumentReceived(inboundId);
                await outbox.PublishAsync(
                    message,
                    new DeliveryOptions { CorrelationId = Guid.NewGuid().ToString("D") }
                );
            }

            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
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

        if (shouldPublish)
        {
            try
            {
                await outbox.FlushOutgoingMessagesAsync();
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Inbound ingress {Operation} {Module} {Result} {Reason}",
                    Operation,
                    Module,
                    "enqueued",
                    "outbox_flush_deferred"
                );
            }
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
