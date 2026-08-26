using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Notifications;
using Wolverine;
using Wolverine.Runtime;

namespace Vinx.Erp.Paguai;

/// <summary>
/// Consumidor do primeiro evento real de domínio Paguai: resolve destinatários
/// com capability de aprovação na entrega e enfileira um alerta in-app por
/// destinatário. Falha de enfileiramento (ex.: template ainda não aprovado) é
/// best-effort com WARNING — a intenção não é estado de negócio.
/// </summary>
public sealed class PaguaiDraftCreatedHandler(
    NotificationIntentService intents,
    PaguaiApproverDirectory approvers,
    ILogger<PaguaiDraftCreatedHandler> logger
)
{
    private const string Operation = "paguai_draft_created";
    private const string Module = "paguai";

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
        )
        {
            logger.LogWarning(
                "Paguai notification {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "rejected",
                "invalid_event"
            );
            return;
        }

        var correlation = Guid.TryParse(envelope.CorrelationId, out var parsed)
            && parsed != Guid.Empty
            ? parsed
            : Guid.NewGuid();
        // O ator da notificação é o solicitante que causou o evento: atribuição
        // honesta em created_by/correlation; destinatários sempre outros usuários.
        var actor = new ActorContext(message.RequesterUserId, correlation);
        var eventKey = PaguaiNotificationKeys.DraftCreatedEventKey(message.DraftId);
        var deepLink = PaguaiNotificationKeys.DraftCreatedDeepLink(
            message.CompanyId,
            message.DraftId
        );

        var recipients = await approvers.ResolveApproverUserIdsAsync(
            message.CompanyId,
            excludeUserId: message.RequesterUserId,
            cancellationToken
        );
        var queued = 0;
        foreach (var recipient in recipients)
        {
            var result = await intents.EnqueueAsync(
                actor,
                new NotificationIntentCommand(
                    message.CompanyId,
                    recipient,
                    eventKey,
                    PaguaiNotificationKeys.DraftCreatedApproverTemplateKey,
                    DeepLinkSelector: deepLink
                ),
                cancellationToken
            );
            if (result.State is NotificationIntentEnqueueState.Enqueued)
            {
                queued++;
                continue;
            }

            logger.LogWarning(
                "Paguai notification {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "deferred",
                result.State.ToString().ToLowerInvariant()
            );
        }

#pragma warning disable CA1873 // Contador barato; o log é o marco operacional do produtor.
        if (queued > 0)
        {
            logger.LogInformation(
                "Paguai notification {Operation} {Module} {Result} {Count}",
                Operation,
                Module,
                "enqueued",
                queued
            );
        }
#pragma warning restore CA1873
    }
}
