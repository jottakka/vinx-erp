using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

internal static class CommitAcknowledgementFailure
{
    private static readonly ConcurrentDictionary<string, byte> ArmedDatabases = new();

    public static void Arm(FoundationPostgresFixture fixture)
    {
        ArmedDatabases.TryAdd(Key(fixture.RuntimeConnectionString), 0);
    }

    public static bool TryConsume(DbConnection? connection) =>
        connection is NpgsqlConnection npgsqlConnection
        && ArmedDatabases.TryRemove(Key(npgsqlConnection.ConnectionString), out _);

    private static string Key(string connectionString)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        return $"{connection.Host}:{connection.Port}/{connection.Database}";
    }
}

internal sealed class CommitAcknowledgementFailureInterceptor : DbTransactionInterceptor
{
    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        if (CommitAcknowledgementFailure.TryConsume(transaction.Connection))
        {
            throw new InvalidOperationException("Test-only commit acknowledgement failure.");
        }

        return Task.CompletedTask;
    }
}
