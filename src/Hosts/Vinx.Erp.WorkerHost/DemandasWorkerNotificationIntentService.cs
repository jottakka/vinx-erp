using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Persistence;
using Wolverine.EntityFrameworkCore;

namespace Vinx.Erp.WorkerHost;

/// <summary>
/// Variante worker-only do serviço de intents: enfileira exclusivamente pela
/// função estreita <c>notifications.enqueue_demandas_notification_intent</c>
/// (a função genérica é revogada do vinx_worker). Registro por tipo — o
/// codegen dos handlers Wolverine não aceita factories opacas na cadeia.
/// </summary>
public sealed class DemandasWorkerNotificationIntentService(
    FoundationDbContext database,
    IDbContextOutbox outbox,
    ILogger<DemandasWorkerNotificationIntentService> logger
) : NotificationIntentService(
    database,
    outbox,
    logger,
    "notifications.enqueue_demandas_notification_intent"
);
