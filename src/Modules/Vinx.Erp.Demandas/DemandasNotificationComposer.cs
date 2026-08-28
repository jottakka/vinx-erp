using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Notifications;

namespace Vinx.Erp.Demandas;

/// <summary>
/// Espécies de aviso compostas pós-commit (flush terminal-only): falha de
/// notificação nunca desfaz a transição da demanda.
/// </summary>
public static class DemandNotificationKinds
{
    public const string Assigned = "assigned";
    public const string Reassigned = "reassigned";
    public const string ParticipantAdded = "participant_added";
    public const string StageAdvanced = "stage_advanced";

    // Etapa de aprovação (fatia approvals) e atraso derivado (fatia worker)
    // compartilham este mesmo compositor com os kind abaixo.
    public const string AwaitingApproval = "awaiting_approval";
    public const string Overdue = "overdue";
}

/// <summary>
/// Compõe intents do Foundation na saída do comando já commitado, com chave
/// estável por ciclo: <c>demanda.&lt;demandId:N&gt;.&lt;cycle&gt;.&lt;kind&gt;</c>
/// — o contador de ciclo renova a deduplicação a cada re-iniciação.
/// </summary>
public sealed class DemandasNotificationComposer(
    NotificationIntentService notificationIntents,
    ILogger<DemandasNotificationComposer> logger
)
{
    /// <summary>
    /// Pré-requisito: templates in-app aprovados <c>demanda.&lt;kind&gt;</c> no
    /// catálogo do Foundation; sem template o enqueue falha fechado (registrado
    /// como Warning, sem afetar o comando).
    /// </summary>
    public async Task ComposeAsync(
        ActorContext actor,
        Guid companyId,
        Guid demandId,
        int cycleCount,
        string kind,
        IReadOnlyList<Guid> recipientUserIds,
        CancellationToken cancellationToken
    )
    {
        var seen = new HashSet<Guid>();
        foreach (var recipient in recipientUserIds)
        {
            if (recipient == Guid.Empty || !seen.Add(recipient))
            {
                continue;
            }

            var result = await notificationIntents.EnqueueAsync(
                actor,
                new NotificationIntentCommand(
                    companyId,
                    recipient,
                    EventKey: BuildKey(demandId, cycleCount, kind),
                    InAppTemplateKey: $"demanda.{kind}",
                    DeepLinkSelector: $"/demandas?demand={demandId:N}"
                ),
                cancellationToken
            );
            if (result.State != NotificationIntentEnqueueState.Enqueued)
            {
                logger.LogWarning(
                    "Demandas {Operation} {Module} {Result} {Reason} {DemandId} {Recipient}",
                    "notification_compose",
                    "demandas",
                    result.State.ToString().ToLowerInvariant(),
                    "intent_not_enqueued",
                    demandId,
                    recipient
                );
            }
        }
    }

    private static string BuildKey(Guid demandId, int cycleCount, string kind) =>
        $"demanda.{demandId:N}.{cycleCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}.{kind}";
}
