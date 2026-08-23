using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

public sealed class FoundationPostgresFixture : IFoundationFixtureLifecycle
{
    private const string BootstrapResourceName =
        "Vinx.Erp.Foundation.IntegrationTests.Infrastructure.bootstrap-foundation-roles.sql";
    private const string AdministratorUsername = "postgres";

    private readonly IFoundationPostgresContainer _container;
    private string? _administratorConnectionString;
    private string? _migratorConnectionString;
    private string? _runtimeConnectionString;
    private string? _workerConnectionString;
    private FoundationPostgresEndpoint? _endpoint;
    private bool _started;
    private bool _disposed;

    public FoundationPostgresFixture()
    {
        var database = $"vinx_fixture_{Guid.NewGuid():N}";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _container = new TestcontainersPostgresContainer(new PostgreSqlBuilder("postgres:17.9-alpine")
            .WithDatabase(database)
            .WithUsername(AdministratorUsername)
            .WithPassword(password)
            .Build());
    }

    internal FoundationPostgresFixture(IFoundationPostgresContainer container)
    {
        _container = container;
    }

    public string MigratorConnectionString => _migratorConnectionString
        ?? throw new InvalidOperationException("The fixture has not been started.");

    public string RuntimeConnectionString => _runtimeConnectionString
        ?? throw new InvalidOperationException("The fixture has not been started.");

    public string WorkerConnectionString => _workerConnectionString
        ?? throw new InvalidOperationException("The fixture has not been started.");

    internal string AdministratorConnectionString => _administratorConnectionString
        ?? throw new InvalidOperationException("The fixture has not been started.");

    internal FoundationPostgresEndpoint Endpoint => _endpoint
        ?? throw new InvalidOperationException("The fixture has not been started.");

    internal static async Task VerifyEndpointAsync(
        NpgsqlConnectionStringBuilder settings,
        FoundationPostgresEndpoint? expectedEndpoint,
        string phase,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port <= 0 || settings.Pooling)
            {
                throw new InvalidOperationException("The endpoint settings are invalid.");
            }

            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT current_database(), current_user, inet_server_port(), version()",
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The endpoint verification returned no row.");
            }

            var database = reader.GetString(0);
            var username = reader.GetString(1);
            var port = reader.GetInt32(2);
            var version = reader.GetString(3);
            if (string.IsNullOrWhiteSpace(database) || port != 5432 ||
                !version.StartsWith("PostgreSQL", StringComparison.Ordinal) ||
                (expectedEndpoint is not null &&
                 (!string.Equals(database, expectedEndpoint.Database, StringComparison.Ordinal) ||
                  !string.Equals(username, expectedEndpoint.Username, StringComparison.Ordinal))))
            {
                throw new InvalidOperationException("The endpoint verification returned unexpected metadata.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"phase={phase} host={settings.Host} port={settings.Port} exception={exception.GetType().Name}",
                exception);
        }
    }

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        try
        {
            await _container.StartAsync();
            var containerSettings = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
            _endpoint = new FoundationPostgresEndpoint(
                _container.Hostname,
                _container.GetMappedPublicPort(5432),
                containerSettings.Database
                    ?? throw new InvalidOperationException("The container database was not configured."),
                containerSettings.Username
                    ?? throw new InvalidOperationException("The container username was not configured."),
                Pooling: false);
            var administratorSettings = new NpgsqlConnectionStringBuilder
            {
                ConnectionString = containerSettings.ConnectionString,
                Host = _endpoint.Host,
                Port = _endpoint.Port,
                Pooling = _endpoint.Pooling
            };
            _administratorConnectionString = administratorSettings.ConnectionString;
            await VerifyEndpointAsync(administratorSettings, _endpoint, "endpoint-connect", CancellationToken.None);

            await using var connection = await OpenAdministratorConnectionAsync();
            await ExecuteBootstrapAsync(connection);

            var migratorPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var runtimePassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var workerPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await SetPasswordAsync(connection, "vinx_migrator", migratorPassword);
            await SetPasswordAsync(connection, "vinx_runtime", runtimePassword);
            await SetPasswordAsync(connection, "vinx_worker", workerPassword);

            _migratorConnectionString = BuildConnectionString("vinx_migrator", migratorPassword);
            _runtimeConnectionString = BuildConnectionString("vinx_runtime", runtimePassword);
            _workerConnectionString = BuildConnectionString("vinx_worker", workerPassword);
            _started = true;
        }
        catch (Exception primaryFailure)
        {
            await FixtureCleanup.CompleteAsync(primaryFailure, DisposeAsync);
            throw;
        }
    }

    public async Task<NpgsqlConnection> OpenAdministratorConnectionAsync()
    {
        var connection = new NpgsqlConnection(_administratorConnectionString
            ?? throw new InvalidOperationException("The fixture has not been started."));
        await connection.OpenAsync();
        return connection;
    }

    public async Task ReapplyBootstrapAsync()
    {
        await using var connection = await OpenAdministratorConnectionAsync();
        await ExecuteBootstrapAsync(connection);
    }

    public Task<bool> MigratorCanSetAuditOwnerAsync() => MigratorCanSetRoleAsync("vinx_audit_owner");

    public Task<bool> MigratorCanSetIdentityOwnerAsync() => MigratorCanSetRoleAsync("vinx_identity_owner");

    public async Task MigrateAsync()
    {
        await using var context = CreateMigratorDbContext();
        await context.Database.MigrateAsync();
    }

    public FoundationDbContext CreateAdministratorDbContext() =>
        CreateDbContext(_administratorConnectionString
            ?? throw new InvalidOperationException("The fixture has not been started."));

    public FoundationDbContext CreateMigratorDbContext() =>
        CreateDbContext(MigratorConnectionString);

    public FoundationDbContext CreateRuntimeDbContext() =>
        CreateDbContext(RuntimeConnectionString);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _container.DisposeAsync();
        _disposed = true;
        _started = false;
    }

    private static async Task ExecuteBootstrapAsync(NpgsqlConnection connection)
    {
        await using var stream = typeof(FoundationPostgresFixture).Assembly
            .GetManifestResourceStream(BootstrapResourceName)
            ?? throw new InvalidOperationException("The Foundation role bootstrap script was not embedded.");
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync();
        await using var command = new NpgsqlCommand(script, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<bool> MigratorCanSetRoleAsync(string roleName)
    {
        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SET ROLE {roleName}; RESET ROLE;", connection);
        await command.ExecuteNonQueryAsync();
        return true;
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
        var builder = new NpgsqlConnectionStringBuilder(_administratorConnectionString
            ?? throw new InvalidOperationException("The fixture has not been started."))
        {
            Username = username,
            Password = password,
            Pooling = false
        };
        return builder.ConnectionString;
    }

    private static FoundationDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<FoundationDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new RowVersionInterceptor())
            .Options;
        return new FoundationDbContext(options);
    }

}

internal interface IFoundationFixtureLifecycle : IAsyncDisposable
{
    string RuntimeConnectionString { get; }

    Task StartAsync();

    Task MigrateAsync();

    FoundationDbContext CreateAdministratorDbContext();
}

internal static class FixtureCleanup
{
    internal static async ValueTask CompleteAsync(
        Exception? primaryFailure,
        params Func<ValueTask>[] cleanupSteps)
    {
        List<Exception>? failures = primaryFailure is null ? null : [primaryFailure];
        foreach (var cleanupStep in cleanupSteps)
        {
            try
            {
                await cleanupStep();
            }
            catch (Exception cleanupFailure)
            {
                (failures ??= []).Add(cleanupFailure);
            }
        }

        if (failures is not { Count: > 0 })
        {
            return;
        }

        if (failures.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(failures);
    }
}

internal interface IFoundationPostgresContainer : IAsyncDisposable
{
    string Hostname { get; }

    string GetConnectionString();

    int GetMappedPublicPort(int containerPort);

    Task StartAsync();
}

internal sealed class TestcontainersPostgresContainer(PostgreSqlContainer container) : IFoundationPostgresContainer
{
    public string Hostname => container.Hostname;

    public string GetConnectionString() => container.GetConnectionString();

    public int GetMappedPublicPort(int containerPort) => container.GetMappedPublicPort(containerPort);

    public Task StartAsync() => container.StartAsync();

    public ValueTask DisposeAsync() => container.DisposeAsync();
}

internal sealed record FoundationPostgresEndpoint(
    string Host,
    int Port,
    string Database,
    string Username,
    bool Pooling);
