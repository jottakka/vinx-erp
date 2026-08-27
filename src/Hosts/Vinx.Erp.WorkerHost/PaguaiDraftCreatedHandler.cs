using System.Data.Common;
using JasperFx.CodeGeneration;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Paguai;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Vinx.Erp.WorkerHost;

/// <summary>Aciona a fronteira Pagaí mínima que resolve aprovadores e cria intenções in-app idempotentes.</summary>
[PaguaiDraftCreatedFailurePolicy]
public sealed class PaguaiDraftCreatedHandler(
    FoundationDbContext database,
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
        var connection = database.Database.GetDbConnection();
        var openedConnection = connection.State is not System.Data.ConnectionState.Open;
        if (openedConnection)
        {
            await database.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT status FROM notifications.enqueue_paguai_draft_created_notification(@company_id, @draft_id, @requester_user_id, @correlation_id)";
            AddParameter(command, "company_id", message.CompanyId);
            AddParameter(command, "draft_id", message.DraftId);
            AddParameter(command, "requester_user_id", message.RequesterUserId);
            AddParameter(command, "correlation_id", correlationId);
            return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
        finally
        {
            if (openedConnection)
            {
                await database.Database.CloseConnectionAsync();
            }
        }
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
