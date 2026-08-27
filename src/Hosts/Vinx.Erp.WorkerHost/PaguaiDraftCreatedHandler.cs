using System.Data.Common;
using JasperFx.CodeGeneration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Paguai;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Vinx.Erp.WorkerHost;

/// <summary>Aciona a fronteira Pagaí mínima que resolve aprovadores e cria intenções in-app idempotentes.</summary>
[PaguaiDraftCreatedFailurePolicy]
public sealed class PaguaiDraftCreatedHandler(
    FoundationDbContext database,
    IDbContextOutbox outbox,
    ILogger<PaguaiDraftCreatedHandler> logger
)
{
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

        string status;
        try
        {
            status = await EnqueueAsync(message, correlationId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState == "22023")
        {
            throw new WorkerPaguaiDraftCreatedDeadLetterException(
                WorkerPaguaiDraftCreatedDeadLetterException.InvalidExceptionType,
                "Paguai draft notification rejected the envelope."
            );
        }

        if (status is "created" or "replay" or "no_current_approvers")
        {
            return;
        }

        if (status == "template_unavailable")
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

        throw new WorkerPaguaiDraftCreatedDeadLetterException(
            WorkerPaguaiDraftCreatedDeadLetterException.InvalidExceptionType,
            "Paguai draft notification rejected the envelope."
        );
    }

    internal static async ValueTask PersistRedactedWorkerDeadLetterAsync(
        IWolverineRuntime runtime,
        IEnvelopeLifecycle lifecycle,
        Exception _,
        string exceptionType,
        string exceptionMessage
    )
    {
        var envelope =
            lifecycle.Envelope
            ?? throw new InvalidOperationException("Worker dead-letter lifecycle has no envelope.");

        WorkerDeadLetterRedactionInterceptor.RedactEnvelope(envelope);
        await runtime.Storage.Inbox.MoveToDeadLetterStorageAsync(
            envelope,
            new WorkerPaguaiDraftCreatedDeadLetterException(exceptionType, exceptionMessage)
        );
        await lifecycle.CompleteAsync();
    }

    internal static ValueTask PersistInvalidDeadLetterAsync(
        IWolverineRuntime runtime,
        IEnvelopeLifecycle lifecycle,
        Exception exception
    ) =>
        PersistRedactedWorkerDeadLetterAsync(
            runtime,
            lifecycle,
            exception,
            WorkerPaguaiDraftCreatedDeadLetterException.InvalidExceptionType,
            "Paguai draft notification rejected the envelope."
        );

    internal static ValueTask PersistRetryExhaustedDeadLetterAsync(
        IWolverineRuntime runtime,
        IEnvelopeLifecycle lifecycle,
        Exception exception
    ) =>
        PersistRedactedWorkerDeadLetterAsync(
            runtime,
            lifecycle,
            exception,
            WorkerPaguaiDraftCreatedDeadLetterException.RetryExhaustedExceptionType,
            "Paguai draft notification exhausted transient retries."
        );

    internal static ValueTask PersistUnexpectedDeadLetterAsync(
        IWolverineRuntime runtime,
        IEnvelopeLifecycle lifecycle,
        Exception exception
    ) =>
        PersistRedactedWorkerDeadLetterAsync(
            runtime,
            lifecycle,
            exception,
            WorkerPaguaiDraftCreatedDeadLetterException.UnexpectedExceptionType,
            "Paguai draft notification failed unexpectedly."
        );

    private async Task<string> EnqueueAsync(
        PaguaiDraftCreated message,
        Guid correlationId,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var connection = database.Database.GetDbConnection();
        var deliveryIds = new List<Guid>();
        string? status = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText =
                "SELECT status, delivery_id FROM notifications.enqueue_paguai_draft_created_notification(@company_id, @draft_id, @requester_user_id, @correlation_id)";
            AddParameter(command, "company_id", message.CompanyId);
            AddParameter(command, "draft_id", message.DraftId);
            AddParameter(command, "requester_user_id", message.RequesterUserId);
            AddParameter(command, "correlation_id", correlationId);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var rowStatus = reader.GetString(0);
                    var deliveryId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
                    if (rowStatus == "created" && deliveryId is { } createdDeliveryId)
                    {
                        status ??= rowStatus;
                        if (status != rowStatus)
                        {
                            throw new InvalidOperationException("Paguai draft notification returned mixed statuses.");
                        }

                        deliveryIds.Add(createdDeliveryId);
                        continue;
                    }

                    if (
                        rowStatus is "replay" or "no_current_approvers" or "template_unavailable"
                        && deliveryId is null
                        && status is null
                    )
                    {
                        status = rowStatus;
                        continue;
                    }

                    throw new InvalidOperationException("Paguai draft notification returned an invalid result row.");
                }
            }

            if (status is null)
            {
                throw new InvalidOperationException("Paguai draft notification returned no result rows.");
            }

            if (deliveryIds.Count > 0)
            {
                outbox.Enroll(database);
                foreach (var deliveryId in deliveryIds)
                {
                    await outbox.PublishAsync(
                        new NotificationDeliveryRequested(deliveryId),
                        new DeliveryOptions { CorrelationId = correlationId.ToString("D") }
                    );
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }

        if (deliveryIds.Count > 0)
        {
            try
            {
                await outbox.FlushOutgoingMessagesAsync();
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Paguai draft dispatch {Operation} {Module} {Result} {Reason}",
                    "draft_created",
                    "paguai",
                    "created",
                    "outbox_flush_deferred"
                );
            }
        }

        return status;
    }

    private static void AddParameter(DbCommand command, string name, Guid value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Classifica o descarte terminal do produtor Pagaí sem persistir detalhes do rascunho.</summary>
internal sealed class WorkerPaguaiDraftCreatedDeadLetterException(
    string exceptionType,
    string exceptionMessage
) : Exception(exceptionMessage), IDeadLetterExceptionInfo
{
    public const string InvalidExceptionType = "paguai_draft_created_invalid";
    public const string RetryExhaustedExceptionType = "paguai_draft_created_retry_exhausted";
    public const string UnexpectedExceptionType = "paguai_draft_created_unexpected";

    public string ExceptionType { get; } = exceptionType;

    public string ExceptionMessage { get; } = exceptionMessage;
}

/// <summary>Retry finito do banco; descarte redigido e tipado para o produtor Pagaí.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class PaguaiDraftCreatedFailurePolicyAttribute : ModifyHandlerChainAttribute
{
    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain
            .OnException<WorkerPaguaiDraftCreatedDeadLetterException>()
            .CustomActionIndefinitely(
                PaguaiDraftCreatedHandler.PersistInvalidDeadLetterAsync,
                "Redact and dead-letter invalid Paguai draft notification"
            );
        chain
            .OnException<NpgsqlException>(exception => exception.IsTransient, "transient_npgsql")
            .ScheduleRetry(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
            .Then.CustomActionIndefinitely(
                PaguaiDraftCreatedHandler.PersistRetryExhaustedDeadLetterAsync,
                "Redact and dead-letter exhausted Paguai draft notification"
            );
        chain
            .OnException<OperationCanceledException>()
            .ScheduleRetry(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
            .Then.CustomActionIndefinitely(
                PaguaiDraftCreatedHandler.PersistRetryExhaustedDeadLetterAsync,
                "Redact and dead-letter cancelled Paguai draft notification"
            );
        chain
            .OnAnyException()
            .CustomActionIndefinitely(
                PaguaiDraftCreatedHandler.PersistUnexpectedDeadLetterAsync,
                "Redact and dead-letter unexpected Paguai draft notification"
            );
    }
}
