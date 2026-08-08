using Npgsql;
using Testcontainers.PostgreSql;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PostgresContainerFixture : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().Build();

    public async Task<int> ExecuteScalarAsync()
    {
        await _container.StartAsync();

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 1", connection);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
