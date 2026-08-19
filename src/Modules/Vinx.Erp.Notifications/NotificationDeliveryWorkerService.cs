using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Notifications;

public sealed record NotificationDeliveryResult(string Status, bool Finalized);

/// <summary>
/// Coordena a entrega de uma notificação: ingresso idempotente, claim único
/// por token de tentativa, chamada ao provider fora de transação aberta e
/// finalização monotônica por token. Timeout do provider vira ambíguo
/// terminal; falha transitiva de infraestrutura propaga para o retry finito
/// do worker.
/// </summary>
public sealed class NotificationDeliveryWorkerService(
    FoundationDbContext database,
    NotificationProviderResolver providers,
    NotificationDeliveryMetrics metrics,
    ILogger<NotificationDeliveryWorkerService> logger
)
{
    private const string Operation = "notification_delivery";
    private const string Module = "notifications";

    public async Task<NotificationDeliveryResult> DeliverAsync(
        Guid deliveryId,
        Guid correlationId,
        CancellationToken cancellationToken
    )
    {
        if (deliveryId == Guid.Empty || correlationId == Guid.Empty)
        {
            throw new NotificationDeliveryPermanentException();
        }

        var acceptance = await ExecuteAsync(
            "SELECT status, transitioned, channel FROM notifications.accept_notification_delivery(@delivery_id, @correlation_id)",
            static reader => new WorkerStep(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)),
            cancellationToken,
            ("delivery_id", deliveryId),
            ("correlation_id", correlationId)
        );
        if (IsSkip(acceptance.Status))
        {
            metrics.RecordOutcome(acceptance.Channel ?? "unknown", "skipped");
            return new NotificationDeliveryResult(acceptance.Status, false);
        }

        var claim = await ExecuteAsync(
            "SELECT status, attempt_id, channel, claimed FROM notifications.claim_notification_delivery(@delivery_id)",
            static reader => new WorkerClaim(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3)),
            cancellationToken,
            ("delivery_id", deliveryId)
        );
        if (IsSkip(claim.Status) || !claim.Claimed || claim.AttemptId is null)
        {
            // Terminal antes do claim, ou lease detido por outra tentativa:
            // sem chamada externa e sem confirmação adicional.
            metrics.RecordOutcome(claim.Channel ?? acceptance.Channel ?? "unknown", "skipped");
            return new NotificationDeliveryResult(claim.Status, false);
        }

        var provider = providers.Resolve(claim.Channel!);
        var stopwatch = Stopwatch.StartNew();
        NotificationProviderResult providerResult;
        try
        {
            providerResult = await provider.DeliverAsync(deliveryId, cancellationToken);
        }
        finally
        {
            stopwatch.Stop();
            metrics.RecordDuration(claim.Channel!, stopwatch.Elapsed.TotalMilliseconds);
        }

        var outcome = providerResult.Kind switch
        {
            NotificationProviderResultKind.Delivered => "delivered",
            NotificationProviderResultKind.Rejected => "failed",
            _ => "ambiguous",
        };
        var finalization = await ExecuteAsync(
            """
            SELECT status, finalized
            FROM notifications.finalize_notification_delivery(
                @delivery_id, @attempt_id, @correlation_id, @outcome)
            """,
            static reader => new NotificationDeliveryResult(reader.GetString(0), reader.GetBoolean(1)),
            cancellationToken,
            ("delivery_id", deliveryId),
            ("attempt_id", claim.AttemptId.Value),
            ("correlation_id", correlationId),
            ("outcome", outcome)
        );
        metrics.RecordOutcome(claim.Channel!, finalization.Status);
        logger.LogInformation(
            "Notification delivery {Operation} {Module} {Result} {Reason}",
            Operation,
            Module,
            finalization.Status,
            finalization.Finalized ? outcome : "already_final"
        );
        return finalization;
    }

    /// <summary>
    /// Executa uma função SQL em transação curta própria; a chamada ao
    /// provider nunca acontece com esta transação aberta.
    /// </summary>
    private async Task<T> ExecuteAsync<T>(
        string commandText,
        Func<DbDataReader, T> read,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        var connection = database.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        try
        {
            T result;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new NotificationDeliveryPermanentException();
                }

                if (string.Equals(reader.GetString(0), "not_found", StringComparison.Ordinal))
                {
                    throw new NotificationDeliveryPermanentException();
                }

                result = read(reader);
            }

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (PostgresException exception)
            when (exception.SqlState
                is PostgresErrorCodes.CheckViolation
                    or PostgresErrorCodes.InvalidParameterValue)
        {
            throw new NotificationDeliveryPermanentException();
        }
    }

    private static bool IsSkip(string status) =>
        status
            is "delivered"
                or "provider_failed"
                or "provider_ambiguous"
                or "provider_in_flight";

    private static void AddParameter(IDbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record WorkerStep(string Status, bool Transitioned, string? Channel);

    private sealed record WorkerClaim(string Status, Guid? AttemptId, string? Channel, bool Claimed);
}
