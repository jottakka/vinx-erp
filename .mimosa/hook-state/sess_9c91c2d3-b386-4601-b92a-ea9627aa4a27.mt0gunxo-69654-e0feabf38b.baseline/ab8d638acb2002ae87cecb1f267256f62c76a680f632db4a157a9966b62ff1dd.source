using Npgsql;

namespace Vinx.Erp.WorkerHost;

internal sealed class WorkerDatabasePreflight(NpgsqlDataSource dataSource) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT session_user, current_user",
            connection
        );
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (
            !await reader.ReadAsync(cancellationToken)
            || !string.Equals(reader.GetString(0), "vinx_worker", StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), "vinx_worker", StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException("Worker database principal is not authorized.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
