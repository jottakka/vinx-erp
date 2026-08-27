using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Paguai;
using Wolverine;

namespace Vinx.Erp.WorkerHost;

/// <summary>Resolve aprovadores atuais no diretório restrito e cria somente intenções in-app idempotentes.</summary>
public sealed class PaguaiDraftCreatedHandler(
    FoundationDbContext database,
    NotificationIntentService notificationIntents,
    ILogger<PaguaiDraftCreatedHandler> logger
)
{
    private const string TemplateKey = "paguai.draft-created";

    public async Task Handle(
        PaguaiDraftCreated message,
        Envelope envelope,
        CancellationToken cancellationToken
    )
    {
        if (
            message.CompanyId == Guid.Empty
            || message.DraftId == Guid.Empty
            || message.RequesterUserId == Guid.Empty
            || !Guid.TryParse(envelope.CorrelationId, out var correlationId)
            || correlationId == Guid.Empty
        )
        {
            logger.LogWarning(
                "Paguai draft dispatch {Operation} {Module} {Result} {Reason}",
                "draft_created",
                "paguai",
                "ignored",
                "invalid_event"
            );
            return;
        }

        var recipientUserIds = await ListRecipientsAsync(message, cancellationToken);
        var eventKey = $"paguai.{message.DraftId:N}.draft-created";
        foreach (var recipientUserId in recipientUserIds)
        {
            var result = await notificationIntents.EnqueueAsync(
                new ActorContext(message.RequesterUserId, correlationId),
                new NotificationIntentCommand(
                    message.CompanyId,
                    recipientUserId,
                    eventKey,
                    TemplateKey
                ),
                cancellationToken
            );
            if (result.State is NotificationIntentEnqueueState.Enqueued)
            {
                continue;
            }

            if (result.State is NotificationIntentEnqueueState.Invalid)
            {
                logger.LogWarning(
                    "Paguai draft dispatch {Operation} {Module} {Result} {Reason}",
                    "draft_created",
                    "paguai",
                    "skipped",
                    "template_unavailable"
                );
                return;
            }

            if (result.State is NotificationIntentEnqueueState.Forbidden)
            {
                logger.LogWarning(
                    "Paguai draft dispatch {Operation} {Module} {Result} {Reason}",
                    "draft_created",
                    "paguai",
                    "skipped",
                    "recipient_no_longer_eligible"
                );
                continue;
            }

            throw new InvalidOperationException("Paguai draft notification dispatch is unavailable.");
        }
    }

    private async Task<IReadOnlyList<Guid>> ListRecipientsAsync(
        PaguaiDraftCreated message,
        CancellationToken cancellationToken
    )
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State is not System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT user_id FROM paguai.list_draft_approver_user_ids(@company_id, @exclude_user_id)";
        AddParameter(command, "company_id", message.CompanyId);
        AddParameter(command, "exclude_user_id", message.RequesterUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var recipients = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            recipients.Add(reader.GetGuid(0));
        }

        return recipients;
    }

    private static void AddParameter(DbCommand command, string name, Guid value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
