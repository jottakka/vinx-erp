using System.Security.Cryptography;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

public sealed class FoundationPostgresFixture : IAsyncDisposable
{
    private const string BootstrapResourceName =
        "Vinx.Erp.Foundation.IntegrationTests.Infrastructure.bootstrap-foundation-roles.sql";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17.9-alpine")
        .Build();
    private string? _migratorConnectionString;
    private string? _runtimeConnectionString;
    private bool _started;

    public string MigratorConnectionString => _migratorConnectionString
        ?? throw new InvalidOperationException("The fixture has not been started.");

    public string RuntimeConnectionString => _runtimeConnectionString
        ?? throw new InvalidOperationException("The fixture has not been started.");

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        await _container.StartAsync();
        await using var connection = await OpenAdministratorConnectionAsync();
        await ExecuteBootstrapAsync(connection);

        var migratorPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var runtimePassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await SetPasswordAsync(connection, "vinx_migrator", migratorPassword);
        await SetPasswordAsync(connection, "vinx_runtime", runtimePassword);

        _migratorConnectionString = BuildConnectionString("vinx_migrator", migratorPassword);
        _runtimeConnectionString = BuildConnectionString("vinx_runtime", runtimePassword);
        _started = true;
    }

    public async Task<NpgsqlConnection> OpenAdministratorConnectionAsync()
    {
        var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    public async Task<bool> MigratorCanSetAuditOwnerAsync()
    {
        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SET ROLE vinx_audit_owner; RESET ROLE;", connection);
        await command.ExecuteNonQueryAsync();
        return true;
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private async Task ExecuteBootstrapAsync(NpgsqlConnection connection)
    {
        await using var stream = typeof(FoundationPostgresFixture).Assembly
            .GetManifestResourceStream(BootstrapResourceName)
            ?? throw new InvalidOperationException("The Foundation role bootstrap script was not embedded.");
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync();
        await using var command = new NpgsqlCommand(script, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetPasswordAsync(NpgsqlConnection connection, string roleName, string password)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using var passwordCommand = new NpgsqlCommand(
            "SELECT set_config('vinx.bootstrap.password', @password, true);", connection, transaction);
        passwordCommand.Parameters.AddWithValue("password", password);
        await passwordCommand.ExecuteNonQueryAsync();

        await using var alterRoleCommand = new NpgsqlCommand(
            $"""
            DO $$
            BEGIN
                EXECUTE format('ALTER ROLE {roleName} PASSWORD %L', current_setting('vinx.bootstrap.password'));
            END
            $$;
            """, connection, transaction);
        await alterRoleCommand.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private string BuildConnectionString(string username, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = username,
            Password = password
        };
        return builder.ConnectionString;
    }
}
