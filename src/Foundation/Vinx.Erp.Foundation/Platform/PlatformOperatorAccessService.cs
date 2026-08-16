using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Platform;

public enum PlatformOperatorAccessState
{
    Allowed,
    Denied,
    Unavailable,
}

public sealed record PlatformOperatorAccessResult(PlatformOperatorAccessState State);

/// <summary>
/// Avalia acesso global de operador exclusivamente pela identidade confiável do ator.
/// </summary>
public sealed class PlatformOperatorAccessService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    ILogger<PlatformOperatorAccessService> logger
)
{
    private const string Operation = "platform_operator_access";
    private const string Module = "foundation";
    private const string EventTemplate =
        "Platform operator access {Operation} {Module} {Result} {Reason}";

    /// <summary>
    /// Consulta descoberta própria sem gerar auditoria e descarta o contexto RLS transacional.
    /// </summary>
    public Task<PlatformOperatorAccessResult> EvaluateAsync(
        ActorContext actor,
        CancellationToken cancellationToken
    ) => EvaluateAsync(actor, auditDenial: false, cancellationToken);

    /// <summary>
    /// Exige operador global; uma negação confirmada gera auditoria global na mesma transação.
    /// </summary>
    public Task<PlatformOperatorAccessResult> RequireAsync(
        ActorContext actor,
        CancellationToken cancellationToken
    ) => EvaluateAsync(actor, auditDenial: true, cancellationToken);

    private async Task<PlatformOperatorAccessResult> EvaluateAsync(
        ActorContext actor,
        bool auditDenial,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
            var isOperator = await database
                .Database.SqlQuery<bool>(
                    $"SELECT foundation.current_actor_is_platform_operator() AS \"Value\""
                )
                .SingleAsync(cancellationToken);
            if (isOperator)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new PlatformOperatorAccessResult(PlatformOperatorAccessState.Allowed);
            }

            if (!auditDenial)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new PlatformOperatorAccessResult(PlatformOperatorAccessState.Denied);
            }

            await auditAppender.AppendAsync(
                new AuditEventData(
                    actor.UserId,
                    AuditScope.Global,
                    null,
                    AuditSource.Web,
                    "platform.operator.required",
                    "platform_operator",
                    null,
                    AuditOutcome.Denied,
                    new CorrelationContext(actor.CorrelationId).CorrelationId,
                    "platform_operator_required",
                    "{}"
                ),
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            return new PlatformOperatorAccessResult(PlatformOperatorAccessState.Denied);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                EventTemplate,
                Operation,
                Module,
                "unavailable",
                "operator_evaluation_failed"
            );
            return new PlatformOperatorAccessResult(PlatformOperatorAccessState.Unavailable);
        }
    }
}
