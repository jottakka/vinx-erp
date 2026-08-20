using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Notifications;

/// <summary>
/// Lê e marca a inbox in-app do ator atual por funções SECURITY DEFINER; o
/// escopo de empresa é recalculado no servidor a partir das memberships ativas
/// e nenhum parâmetro do cliente amplia o conjunto visível.
/// </summary>
public sealed class NotificationInboxService(
    FoundationDbContext database,
    ILogger<NotificationInboxService> logger
)
{
    private const string Operation = "notification_inbox";
    private const string Module = "foundation";
    private const int DefaultPageSize = 20;

    public async Task<NotificationInboxResult> ListAsync(
        ActorContext actor,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken
    )
    {
        var effectiveLimit = limit ?? DefaultPageSize;
        if (effectiveLimit is < 1 or > 50)
        {
            return new NotificationInboxResult(NotificationInboxState.Invalid);
        }

        DateTimeOffset cursorCreatedAt;
        Guid cursorId;
        var hasCursor = cursor is not null;
        if (!hasCursor)
        {
            cursorCreatedAt = default;
            cursorId = Guid.Empty;
        }
        else if (!NotificationInboxCursor.TryDecode(cursor, out cursorCreatedAt, out cursorId))
        {
            return new NotificationInboxResult(NotificationInboxState.Invalid);
        }

        try
        {
            var rows = await ExecuteListAsync(actor, effectiveLimit, cursorCreatedAt, cursorId, hasCursor, cancellationToken);
            var pageItems = rows.Take(effectiveLimit).ToList();
            var nextCursor =
                rows.Count > effectiveLimit && pageItems.Count > 0
                    ? NotificationInboxCursor.Encode(
                        pageItems[^1].CreatedAt,
                        pageItems[^1].Id
                    )
                    : null;
            return new NotificationInboxResult(
                NotificationInboxState.Listed,
                new NotificationInboxPage(pageItems, nextCursor)
            );
        }
        catch (PostgresException exception)
        {
            return ClassifyListFailure(exception);
        }
        catch (Exception exception)
        {
            LogUnavailable(exception, "list");
            return new NotificationInboxResult(NotificationInboxState.Unavailable);
        }
    }

    public async Task<NotificationUnreadCountResult> CountUnreadAsync(
        ActorContext actor,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var unreadCount = await ExecuteCountAsync(actor, cancellationToken);
            return new NotificationUnreadCountResult(
                NotificationUnreadCountState.Counted,
                unreadCount
            );
        }
        catch (PostgresException exception)
        {
            if (IsActorRequired(exception))
            {
                return new NotificationUnreadCountResult(NotificationUnreadCountState.Unauthorized);
            }

            LogUnavailable(exception, "count");
            return new NotificationUnreadCountResult(NotificationUnreadCountState.Unavailable);
        }
        catch (Exception exception)
        {
            LogUnavailable(exception, "count");
            return new NotificationUnreadCountResult(NotificationUnreadCountState.Unavailable);
        }
    }

    public async Task<NotificationMarkReadResult> MarkReadAsync(
        ActorContext actor,
        Guid deliveryId,
        CancellationToken cancellationToken
    )
    {
        if (deliveryId == Guid.Empty)
        {
            return new NotificationMarkReadResult(NotificationMarkReadState.Invalid);
        }

        try
        {
            var status = await ExecuteMarkReadAsync(actor, deliveryId, cancellationToken);
            return status switch
            {
                "read" or "already_read" => new NotificationMarkReadResult(
                    NotificationMarkReadState.Read
                ),
                _ => new NotificationMarkReadResult(NotificationMarkReadState.NotFound),
            };
        }
        catch (PostgresException exception)
        {
            if (IsActorRequired(exception))
            {
                return new NotificationMarkReadResult(NotificationMarkReadState.Unauthorized);
            }

            if (IsInvalidParameter(exception))
            {
                return new NotificationMarkReadResult(NotificationMarkReadState.Invalid);
            }

            LogUnavailable(exception, "mark_read");
            return new NotificationMarkReadResult(NotificationMarkReadState.Unavailable);
        }
        catch (Exception exception)
        {
            LogUnavailable(exception, "mark_read");
            return new NotificationMarkReadResult(NotificationMarkReadState.Unavailable);
        }
    }

    private async Task<List<NotificationInboxItem>> ExecuteListAsync(
        ActorContext actor,
        int limit,
        DateTimeOffset cursorCreatedAt,
        Guid cursorId,
        bool hasCursor,
        CancellationToken cancellationToken
    )
    {
        // Leitura consistente em transação curta descartada sem commit, no
        // padrão do resolver de autorização.
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
        var connection = database.Database.GetDbConnection();
        await using var sql = connection.CreateCommand();
        sql.Transaction = transaction.GetDbTransaction();
        sql.CommandText = """
            SELECT id, company_id, event_key, deep_link_selector, created_at, read_at
            FROM notifications.list_inbox_notifications(
                @limit, @cursor_created_at, @cursor_id)
            """;
        AddParameter(sql, "limit", limit);
        AddParameter(sql, "cursor_created_at", hasCursor ? cursorCreatedAt : DBNull.Value);
        AddParameter(sql, "cursor_id", hasCursor ? cursorId : DBNull.Value);
        var items = new List<NotificationInboxItem>();
        await using (var reader = await sql.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(
                    NotificationInboxPresentation.Project(
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.GetFieldValue<DateTimeOffset>(4),
                        reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)
                    )
                );
            }
        }

        // O reader precisa ser descartado antes de devolver a transação.
        await transaction.RollbackAsync(cancellationToken);
        return items;
    }

    private async Task<long> ExecuteCountAsync(
        ActorContext actor,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
        var connection = database.Database.GetDbConnection();
        await using var sql = connection.CreateCommand();
        sql.Transaction = transaction.GetDbTransaction();
        sql.CommandText = "SELECT unread_count FROM notifications.count_unread_inbox_notifications()";
        var result = await sql.ExecuteScalarAsync(cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return (long)result!;
    }

    private async Task<string> ExecuteMarkReadAsync(
        ActorContext actor,
        Guid deliveryId,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
        var connection = database.Database.GetDbConnection();
        await using var sql = connection.CreateCommand();
        sql.Transaction = transaction.GetDbTransaction();
        sql.CommandText = """
            SELECT status FROM notifications.mark_inbox_notification_read(@delivery_id)
            """;
        AddParameter(sql, "delivery_id", deliveryId);
        var status = (string)(await sql.ExecuteScalarAsync(cancellationToken))!;
        // Somente a transição efetiva audita e confirma; ausente/already_read
        // não grava nada e pode descartar a transação.
        if (status == "read")
        {
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }

        return status;
    }

    private NotificationInboxResult ClassifyListFailure(PostgresException exception)
    {
        if (IsActorRequired(exception))
        {
            return new NotificationInboxResult(NotificationInboxState.Unauthorized);
        }

        if (IsInvalidParameter(exception))
        {
            return new NotificationInboxResult(NotificationInboxState.Invalid);
        }

        LogUnavailable(exception, "list");
        return new NotificationInboxResult(NotificationInboxState.Unavailable);
    }

    private void LogUnavailable(Exception exception, string operation) =>
        logger.LogError(
            exception,
            "Notification inbox {Operation} {Module} {Result} {Reason}",
            operation,
            Module,
            "unavailable",
            "database_or_audit_unavailable"
        );

    private static bool IsActorRequired(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.InsufficientPrivilege
        && exception.MessageText == "notification actor is required";

    private static bool IsInvalidParameter(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.InvalidParameterValue
        && exception.MessageText == "inbox parameters are invalid";

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
