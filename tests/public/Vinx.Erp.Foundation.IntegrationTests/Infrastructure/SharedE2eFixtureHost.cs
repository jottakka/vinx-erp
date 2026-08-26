using System.Text;
using System.Runtime.Versioning;
using Npgsql;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

/// <summary>
/// Owns one prepared PostgreSQL fixture and the two fixed-principal E2E ApiHosts that share it.
/// </summary>
public sealed class SharedE2eFixtureHost : IAsyncDisposable
{
    private readonly object _disposeLock = new();
    private FoundationPostgresFixture? _fixtureOwner;
    private E2eFixtureHost? _mariaHost;
    private E2eFixtureHost? _claraHost;
    private E2eDatabaseCredentialFile? _credentialFile;
    private E2eDatasetReceipt? _receipt;
    private Task? _disposeTask;

    private SharedE2eFixtureHost()
    {
    }

    public Uri MariaOrigin => RequireMariaHost().Origin;

    public Uri ClaraOrigin => RequireClaraHost().Origin;

    public string DatabaseHost => RequireFixtureOwner().Endpoint.Host;

    public int DatabasePort => RequireFixtureOwner().Endpoint.Port;

    public string DatabaseName => RequireFixtureOwner().Endpoint.Database;

    public string DatabaseUser => RequireFixtureOwner().Endpoint.Username;

    public string DatabasePasswordFilePath => RequireCredentialFile().Path;

    internal E2eDatasetReceipt Receipt => _receipt
        ?? throw new InvalidOperationException("The shared fixture receipt is not available.");

    internal Task<NpgsqlConnection> OpenAdministratorConnectionAsync() =>
        RequireFixtureOwner().OpenAdministratorConnectionAsync();

    public static async Task<SharedE2eFixtureHost> StartAsync(CancellationToken cancellationToken)
    {
        var sharedHost = new SharedE2eFixtureHost();
        var phase = E2eFixtureHostFailurePhase.FixtureStart;
        try
        {
            sharedHost._fixtureOwner = new FoundationPostgresFixture();
            await sharedHost._fixtureOwner.StartAsync();
            await sharedHost._fixtureOwner.MigrateAsync();

            phase = E2eFixtureHostFailurePhase.Seed;
            var receipt = await E2eFixtureSeeder.SeedAsync(sharedHost._fixtureOwner, cancellationToken);
            await E2eFixtureSeeder.VerifyAsync(sharedHost._fixtureOwner, receipt, cancellationToken);
            sharedHost._receipt = receipt;

            var mariaLease = sharedHost._fixtureOwner.CreateNonOwningLease();
            var mariaContext = FoundationApiFactory.CreateWithPreparedE2eProfile(
                "maria",
                mariaLease,
                receipt);
            sharedHost._mariaHost = await E2eFixtureHost.StartPreparedAsync(
                mariaContext,
                mariaLease,
                cancellationToken);

            var claraLease = sharedHost._fixtureOwner.CreateNonOwningLease();
            var claraContext = FoundationApiFactory.CreateWithPreparedE2eProfile(
                "clara",
                claraLease,
                receipt);
            sharedHost._claraHost = await E2eFixtureHost.StartPreparedAsync(
                claraContext,
                claraLease,
                cancellationToken);

            phase = E2eFixtureHostFailurePhase.CredentialExport;
            var administratorSettings = new NpgsqlConnectionStringBuilder(
                sharedHost._fixtureOwner.AdministratorConnectionString);
            var credentialValue = administratorSettings.Password;
            if (string.IsNullOrEmpty(credentialValue))
            {
                throw new InvalidOperationException("The fixture administrator credential is unavailable.");
            }

            sharedHost._credentialFile = await E2eDatabaseCredentialFile.CreateAsync(
                credentialValue);
            return sharedHost;
        }
        catch (E2eFixtureHostException exception)
        {
            var combinedFailure = await sharedHost.DisposeAfterStartFailureAsync(exception);
            if (ReferenceEquals(combinedFailure, exception))
            {
                throw;
            }

            throw new E2eFixtureHostException(exception.Phase, combinedFailure);
        }
        catch (Exception primaryFailure)
        {
            var combinedFailure = await sharedHost.DisposeAfterStartFailureAsync(primaryFailure);
            throw new E2eFixtureHostException(phase, combinedFailure);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= DisposeWithRetryStateAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeWithRetryStateAsync()
    {
        try
        {
            await FixtureCleanup.CompleteAsync(
                primaryFailure: null,
                DisposeClaraAsync,
                DisposeMariaAsync,
                DisposeCredentialFileAsync,
                DisposeFixtureOwnerAsync);
        }
        catch
        {
            lock (_disposeLock)
            {
                _disposeTask = null;
            }

            throw;
        }
    }

    private async Task<Exception> DisposeAfterStartFailureAsync(Exception primaryFailure)
    {
        try
        {
            await DisposeAsync();
        }
        catch (Exception cleanupFailure)
        {
            return new AggregateException(primaryFailure, cleanupFailure);
        }

        return primaryFailure;
    }

    private async ValueTask DisposeClaraAsync()
    {
        var claraHost = _claraHost;
        if (claraHost is null)
        {
            return;
        }

        await claraHost.DisposeAsync();
        _claraHost = null;
    }

    private async ValueTask DisposeMariaAsync()
    {
        var mariaHost = _mariaHost;
        if (mariaHost is null)
        {
            return;
        }

        await mariaHost.DisposeAsync();
        _mariaHost = null;
    }

    private async ValueTask DisposeCredentialFileAsync()
    {
        var credentialFile = _credentialFile;
        if (credentialFile is null)
        {
            return;
        }

        await credentialFile.DisposeAsync();
        _credentialFile = null;
    }

    private async ValueTask DisposeFixtureOwnerAsync()
    {
        var fixtureOwner = _fixtureOwner;
        if (fixtureOwner is null)
        {
            return;
        }

        await fixtureOwner.DisposeAsync();
        _fixtureOwner = null;
    }

    private FoundationPostgresFixture RequireFixtureOwner() => _fixtureOwner
        ?? throw new InvalidOperationException("The shared fixture host is not running.");

    private E2eFixtureHost RequireMariaHost() => _mariaHost
        ?? throw new InvalidOperationException("The Maria API host is not running.");

    private E2eFixtureHost RequireClaraHost() => _claraHost
        ?? throw new InvalidOperationException("The Clara API host is not running.");

    private E2eDatabaseCredentialFile RequireCredentialFile() => _credentialFile
        ?? throw new InvalidOperationException("The protected credential file is not available.");
}

/// <summary>
/// Stores an administrative password only in a private temporary file for the lifetime of one shared fixture.
/// </summary>
internal sealed class E2eDatabaseCredentialFile : IAsyncDisposable
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _directoryPath;
    private bool _disposed;

    private E2eDatabaseCredentialFile(string directoryPath, string path)
    {
        _directoryPath = directoryPath;
        Path = path;
    }

    public string Path { get; }

    internal static async Task<E2eDatabaseCredentialFile> CreateAsync(string value)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Shared E2E credentials require Unix file modes.");
        }

        if (string.IsNullOrEmpty(value) || value.Any(char.IsControl))
        {
            throw new ArgumentException("The credential value is invalid.", nameof(value));
        }

        DirectoryInfo? directory = null;
        string? filePath = null;
        try
        {
            directory = Directory.CreateTempSubdirectory("vinx-e2e-");
            ValidatePath(directory.FullName, nameof(directory));
            File.SetUnixFileMode(directory.FullName, PrivateDirectoryMode);
            VerifyDirectoryMode(directory.FullName);

            filePath = System.IO.Path.Combine(directory.FullName, "postgres-password");
            ValidatePath(filePath, nameof(filePath));
            await using (var stream = new FileStream(filePath, new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
                UnixCreateMode = PrivateFileMode,
            }))
            {
                File.SetUnixFileMode(filePath, PrivateFileMode);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(value));
                await stream.FlushAsync();
            }

            VerifyRegularFile(filePath);
            return new E2eDatabaseCredentialFile(directory.FullName, filePath);
        }
        catch (Exception primaryFailure)
        {
            if (directory is null)
            {
                throw;
            }

            try
            {
                DeleteExact(directory.FullName, filePath);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(primaryFailure, cleanupFailure);
            }

            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Shared E2E credentials require Unix file modes.");
        }

        DeleteExact(_directoryPath, Path);
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    [UnsupportedOSPlatform("windows")]
    private static void DeleteExact(string directoryPath, string? filePath)
    {
        ValidatePath(directoryPath, nameof(directoryPath));
        if (!Directory.Exists(directoryPath))
        {
            throw new InvalidOperationException("The protected credential directory was missing.");
        }

        var entries = Directory.GetFileSystemEntries(directoryPath);
        if (filePath is null)
        {
            if (entries.Length != 0)
            {
                throw new InvalidOperationException("The protected credential directory contains an unexpected entry.");
            }
        }
        else
        {
            ValidatePath(filePath, nameof(filePath));
            if (entries.Length != 1
                || !string.Equals(entries[0], filePath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The protected credential directory contains an unexpected entry.");
            }

            VerifyRegularFile(filePath);
            File.Delete(filePath);
        }

        Directory.Delete(directoryPath, recursive: false);
    }

    [UnsupportedOSPlatform("windows")]
    private static void VerifyDirectoryMode(string directoryPath)
    {
        if (Directory.GetParent(directoryPath) is null
            || File.GetUnixFileMode(directoryPath) != PrivateDirectoryMode)
        {
            throw new InvalidOperationException("The protected credential directory mode is invalid.");
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void VerifyRegularFile(string filePath)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists
            || file.LinkTarget is not null
            || (File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0
            || File.GetUnixFileMode(filePath) != PrivateFileMode)
        {
            throw new InvalidOperationException("The protected credential file is invalid.");
        }
    }

    private static void ValidatePath(string path, string parameterName)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
        {
            throw new ArgumentException("The protected credential path is invalid.", parameterName);
        }
    }
}
