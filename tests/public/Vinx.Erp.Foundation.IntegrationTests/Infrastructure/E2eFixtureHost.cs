using System.Net;
using System.Text.Json;
using Npgsql;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

public enum E2eFixtureHostFailurePhase
{
    FixtureStart,
    Seed,
    HostStart,
    Readback,
    CredentialExport,
    Cleanup,
}

public sealed class E2eFixtureHostException(
    E2eFixtureHostFailurePhase phase,
    Exception innerException)
    : Exception("E2E fixture host failed.", innerException)
{
    public E2eFixtureHostFailurePhase Phase { get; } = phase;
}

public enum E2eFixtureHostMode
{
    Single,
    Shared,
}

public sealed record E2eFixtureHostRequest(E2eFixtureHostMode Mode, string? Profile);

public sealed record E2eSharedFixtureReadiness(
    Uri MariaOrigin,
    Uri ClaraOrigin,
    string DatabaseHost,
    int DatabasePort,
    string DatabaseName,
    string DatabaseUser,
    string DatabasePasswordFilePath);

public static class E2eFixtureHostOptions
{
    /// <summary>Aceita somente um profile individual allow-listed ou o par compartilhado Maria/Clara.</summary>
    public static bool TryParse(string[] arguments, out E2eFixtureHostRequest? request)
    {
        request = null;
        if (arguments is ["--profile", var candidate]
            && E2eFixtureProfileCatalog.TryGet(candidate, out _))
        {
            request = new E2eFixtureHostRequest(E2eFixtureHostMode.Single, candidate);
            return true;
        }

        if (arguments is ["--profiles", "maria,clara"])
        {
            request = new E2eFixtureHostRequest(E2eFixtureHostMode.Shared, null);
            return true;
        }

        return false;
    }
}

public static class E2eFixtureHostProcessPolicy
{
    /// <summary>Reconhece apenas cancelamento solicitado sem falha agregada de cleanup.</summary>
    public static bool IsCleanCancellation(Exception exception, bool cancellationRequested) =>
        cancellationRequested
        && (exception is OperationCanceledException
            || exception is E2eFixtureHostException
            {
                InnerException: OperationCanceledException,
            });

    /// <summary>Tenta uma vez os recursos pendentes e preserva a primeira falha de cleanup.</summary>
    public static async Task DisposeWithOneRetryAsync(IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Exception? firstFailure = null;
        try
        {
            await resource.DisposeAsync();
            return;
        }
        catch (Exception exception)
        {
            firstFailure = exception;
        }

        try
        {
            await resource.DisposeAsync();
        }
        catch (Exception retryFailure)
        {
            throw new AggregateException(firstFailure, retryFailure);
        }

        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure!).Throw();
    }
}

/// <summary>
/// Suprime logs do host filho e preserva apenas os canais fixos de prontidão e falha.
/// </summary>
public sealed class E2eFixtureHostOutput : IDisposable
{
    private readonly TextWriter _standardOutput;
    private readonly TextWriter _standardError;
    private int _disposed;

    private E2eFixtureHostOutput(TextWriter standardOutput, TextWriter standardError)
    {
        _standardOutput = standardOutput;
        _standardError = standardError;
    }

    public static E2eFixtureHostOutput SuppressHostConsole()
    {
        var standardOutput = Console.Out;
        var standardError = Console.Error;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        return new E2eFixtureHostOutput(standardOutput, standardError);
    }

    public Task WriteReadinessAsync(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return WriteLineAsync(
            _standardOutput,
            $"E2E_API_ORIGIN={origin.GetLeftPart(UriPartial.Authority)}");
    }

    public Task WriteSingleReadinessAsync(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return WriteLineAsync(
            _standardOutput,
            string.Join(
                Environment.NewLine,
                [
                    $"E2E_API_ORIGIN={origin.GetLeftPart(UriPartial.Authority)}",
                    "E2E_SINGLE_READY=1",
                ]));
    }

    public Task WriteSharedReadinessAsync(E2eSharedFixtureReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        return WriteLineAsync(
            _standardOutput,
            string.Join(
                Environment.NewLine,
                [
                    $"E2E_API_ORIGIN_MARIA={GetLoopbackOrigin(readiness.MariaOrigin)}",
                    $"E2E_API_ORIGIN_CLARA={GetLoopbackOrigin(readiness.ClaraOrigin)}",
                    $"E2E_DB_HOST={GetLoopbackDatabaseHost(readiness.DatabaseHost)}",
                    $"E2E_DB_PORT={GetValidPort(readiness.DatabasePort)}",
                    $"E2E_DB_DATABASE={GetDatabaseName(readiness.DatabaseName)}",
                    $"E2E_DB_USER={GetDatabaseUser(readiness.DatabaseUser)}",
                    $"E2E_DB_PASSWORD_FILE={GetCredentialFilePath(readiness.DatabasePasswordFilePath)}",
                    "E2E_SHARED_READY=1",
                ]));
    }

    public Task WriteFailureAsync(E2eFixtureHostFailurePhase phase)
    {
        var category = phase switch
        {
            E2eFixtureHostFailurePhase.FixtureStart => "fixture_start_failed",
            E2eFixtureHostFailurePhase.Seed => "seed_failed",
            E2eFixtureHostFailurePhase.HostStart => "host_start_failed",
            E2eFixtureHostFailurePhase.Readback => "readback_failed",
            E2eFixtureHostFailurePhase.CredentialExport => "credential_export_failed",
            E2eFixtureHostFailurePhase.Cleanup => "cleanup_failed",
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };
        return WriteLineAsync(_standardError, $"E2E_HOST_FAILED={category}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Console.SetOut(_standardOutput);
        Console.SetError(_standardError);
    }

    private static async Task WriteLineAsync(TextWriter writer, string value)
    {
        await writer.WriteLineAsync(value);
        await writer.FlushAsync();
    }

    private static string GetLoopbackOrigin(Uri origin)
    {
        if (!string.Equals(origin.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !string.Equals(origin.Host, IPAddress.Loopback.ToString(), StringComparison.Ordinal)
            || origin.Port is < 1 or > 65535
            || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment))
        {
            throw new ArgumentException("The shared readiness origin is invalid.", nameof(origin));
        }

        return origin.GetLeftPart(UriPartial.Authority);
    }

    private static string GetLoopbackDatabaseHost(string host) => host is "127.0.0.1" or "localhost"
        ? host
        : throw new ArgumentException("The shared database host is invalid.", nameof(host));

    private static int GetValidPort(int port) => port is >= 1 and <= 65535
        ? port
        : throw new ArgumentOutOfRangeException(nameof(port));

    private static string GetDatabaseName(string databaseName)
    {
        const string prefix = "vinx_fixture_";
        if (string.IsNullOrEmpty(databaseName) || !databaseName.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("The shared database name is invalid.", nameof(databaseName));
        }

        var suffix = databaseName[prefix.Length..];
        return suffix.Length > 0
            && suffix.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? databaseName
            : throw new ArgumentException("The shared database name is invalid.", nameof(databaseName));
    }

    private static string GetDatabaseUser(string databaseUser) =>
        string.Equals(databaseUser, "postgres", StringComparison.Ordinal)
            ? databaseUser
            : throw new ArgumentException("The shared database user is invalid.", nameof(databaseUser));

    private static string GetCredentialFilePath(string path) =>
        Path.IsPathFullyQualified(path)
        && !path.Any(char.IsControl)
            ? path
            : throw new ArgumentException("The credential file path is invalid.", nameof(path));
}

public sealed class E2eFixtureHost : IAsyncDisposable
{
    private static readonly TimeSpan ReadbackTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReadbackStageTimeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _client;
    private readonly object _disposeLock = new();
    private readonly FoundationApiFactory _factory;
    private readonly IFoundationFixtureAccess _fixtureAccess;
    private Task? _disposeTask;

    private E2eFixtureHost(
        E2eFixtureFactoryContext context,
        IFoundationFixtureAccess fixtureAccess,
        HttpClient client,
        Uri origin)
    {
        _factory = context.Factory;
        _fixtureAccess = fixtureAccess;
        _client = client;
        Origin = origin;
        ProfileName = context.Profile.Name;
        Receipt = context.Receipt;
        DatabaseHost = fixtureAccess.Endpoint.Host;
        DatabasePort = fixtureAccess.Endpoint.Port;
        DatabaseName = fixtureAccess.Endpoint.Database;
    }

    public Uri Origin { get; }
    public string ProfileName { get; }
    public E2eDatasetReceipt Receipt { get; }
    internal string DatabaseHost { get; }
    internal int DatabasePort { get; }
    internal string DatabaseName { get; }

    public static async Task<E2eFixtureHost> StartAsync(
        string profileName,
        CancellationToken cancellationToken)
    {
        E2eFixtureFactoryContext? context = null;
        try
        {
            context = await FoundationApiFactory.CreateWithE2eProfileAsync(
                profileName,
                cancellationToken);
            return await StartPreparedAsync(context, context.Factory.Fixture, cancellationToken);
        }
        catch (ArgumentException) when (context is null)
        {
            throw;
        }
        catch (E2eFixtureHostException)
        {
            throw;
        }
        catch (Exception primaryFailure)
        {
            var combinedFailure = await DisposeStartFailureAsync(
                context,
                client: null,
                primaryFailure);
            throw new E2eFixtureHostException(E2eFixtureHostFailurePhase.FixtureStart, combinedFailure);
        }
    }

    internal static async Task<E2eFixtureHost> StartPreparedAsync(
        E2eFixtureFactoryContext context,
        IFoundationFixtureAccess fixtureAccess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fixtureAccess);
        HttpClient? client = null;
        var phase = E2eFixtureHostFailurePhase.HostStart;
        try
        {
            context.Factory.UseKestrel(0);
            client = context.Factory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            var origin = RequireLoopbackOrigin(client.BaseAddress);

            phase = E2eFixtureHostFailurePhase.Readback;
            using var readbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            readbackCancellation.CancelAfter(ReadbackTimeout);
            try
            {
                await VerifyReadbackAsync(
                    client,
                    context.Profile,
                    context.Receipt,
                    readbackCancellation.Token);
            }
            catch (OperationCanceledException exception) when (
                !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"The E2E readback sequence exceeded its overall budget of {ReadbackTimeout.TotalSeconds:0}s.",
                    exception);
            }

            return new E2eFixtureHost(context, fixtureAccess, client, origin);
        }
        catch (Exception primaryFailure)
        {
            var combinedFailure = await DisposeStartFailureAsync(context, client, primaryFailure);
            throw new E2eFixtureHostException(phase, combinedFailure);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            Task disposeTask;
            if (_disposeTask is null)
            {
                disposeTask = DisposeCoreAsync();
                _disposeTask = disposeTask;
                _ = disposeTask.ContinueWith(
                    completedTask =>
                    {
                        if (!completedTask.IsFaulted && !completedTask.IsCanceled)
                        {
                            return;
                        }

                        lock (_disposeLock)
                        {
                            if (ReferenceEquals(_disposeTask, completedTask))
                            {
                                _disposeTask = null;
                            }
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                disposeTask = _disposeTask;
            }

            return new ValueTask(disposeTask);
        }
    }

    internal static (Guid CompanyId, string CompanyName)[] ExpectedCompaniesForProfile(
        string profileName,
        E2eDatasetReceipt receipt) => profileName switch
        {
            "maria" =>
            [
                (receipt.AlfaCompanyId, "Alfa"),
                (receipt.BetaCompanyId, "Beta"),
            ],
            "beatriz" => [(receipt.AlfaCompanyId, "Alfa")],
            "joao" => [(receipt.BetaCompanyId, "Beta")],
            "clara" => [(receipt.GamaCompanyId, "Gama")],
            _ => throw new ArgumentException("Unknown E2E fixture profile.", nameof(profileName)),
        };

    internal async Task<bool> WolverineStorageExistsAsync()
    {
        await using var connection = await _fixtureAccess.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT to_regclass('vinx_wolverine.wolverine_nodes') IS NOT NULL
                OR to_regclass('vinx_wolverine.wolverine_outgoing_envelopes') IS NOT NULL
                OR to_regclass('vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery') IS NOT NULL
            """,
            connection);
        return await command.ExecuteScalarAsync() is true;
    }

    private static Uri RequireLoopbackOrigin(Uri? origin)
    {
        if (origin is null
            || !string.Equals(origin.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !string.Equals(origin.Host, IPAddress.Loopback.ToString(), StringComparison.Ordinal)
            || origin.Port <= 0
            || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment))
        {
            throw new InvalidOperationException("E2E fixture origin is not an HTTP loopback origin.");
        }

        return origin;
    }

    private static async Task VerifyReadbackAsync(
        HttpClient client,
        E2eFixtureProfile profile,
        E2eDatasetReceipt receipt,
        CancellationToken cancellationToken)
    {
        using (var health = await RunReadbackStageAsync(
            "health",
            token => client.GetAsync("/health", token),
            cancellationToken))
        {
            if (health.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException("E2E health readback failed.");
            }
        }

        using (var bootstrap = await ReadSuccessJsonAsync(
            client,
            "bootstrap",
            "/foundation/bootstrap",
            cancellationToken))
        {
            var root = bootstrap.RootElement;
            var companies = root.GetProperty("companies").EnumerateArray().ToArray();
            var expectedCompanies = ExpectedCompaniesForProfile(profile.Name, receipt);
            if (root.GetProperty("user").GetProperty("id").GetGuid() != profile.UserId
                || !string.Equals(
                    root.GetProperty("user").GetProperty("displayName").GetString(),
                    profile.DisplayName,
                    StringComparison.Ordinal)
                || !companies
                    .Select(company => company.GetProperty("companyName").GetString())
                    .SequenceEqual(expectedCompanies.Select(company => company.CompanyName))
                || !companies
                    .Select(company => company.GetProperty("companyId").GetGuid())
                    .SequenceEqual(expectedCompanies.Select(company => company.CompanyId)))
            {
                throw new InvalidOperationException("E2E bootstrap readback failed.");
            }
        }

        if (string.Equals(profile.Name, "beatriz", StringComparison.Ordinal))
        {
            await VerifyBeatrizReadbackAsync(client, receipt, cancellationToken);
            return;
        }

        if (string.Equals(profile.Name, "joao", StringComparison.Ordinal))
        {
            await VerifyJoaoReadbackAsync(client, receipt, cancellationToken);
            return;
        }

        if (string.Equals(profile.Name, "clara", StringComparison.Ordinal))
        {
            await VerifyClaraReadbackAsync(client, receipt, cancellationToken);
            return;
        }

        using (var employees = await ReadSuccessJsonAsync(
            client,
            "employees",
            $"/foundation/companies/{receipt.AlfaCompanyId}/employees",
            cancellationToken))
        {
            var names = employees.RootElement
                .EnumerateArray()
                .Select(employee => employee.GetProperty("displayName").GetString())
                .OrderBy(name => name, StringComparer.Ordinal);
            if (!names.SequenceEqual(["Beatriz Costa", "Maria Silva"]))
            {
                throw new InvalidOperationException("E2E employee readback failed.");
            }
        }

        using (var paguai = await ReadSuccessJsonAsync(
            client,
            "paguai",
            $"/paguai/requests?companyId={receipt.BetaCompanyId}",
            cancellationToken))
        {
            var drafts = paguai.RootElement.EnumerateArray().ToArray();
            if (drafts.Length != 1
                || drafts[0].GetProperty("companyId").GetGuid() != receipt.BetaCompanyId
                || !string.Equals(
                    drafts[0].GetProperty("title").GetString(),
                    "Serviços contábeis",
                    StringComparison.Ordinal)
                || drafts[0].GetProperty("amount").ValueKind != JsonValueKind.String
                || !string.Equals(
                    drafts[0].GetProperty("amount").GetString(),
                    "8500.00",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("E2E Paguai readback failed.");
            }
        }

        using (var atendimento = await ReadSuccessJsonAsync(
            client,
            "atendimento",
            $"/atendimento/cards?companyId={receipt.AlfaCompanyId}",
            cancellationToken))
        {
            var cards = atendimento.RootElement.EnumerateArray().ToArray();
            if (cards.Length != 3
                || cards.Any(card =>
                    card.GetProperty("companyId").GetGuid() != receipt.AlfaCompanyId)
                || !cards
                    .Select(card => card.GetProperty("column").GetString())
                    .OrderBy(column => column, StringComparer.Ordinal)
                    .SequenceEqual(["concluido", "em_andamento", "novo"]))
            {
                throw new InvalidOperationException("E2E Atendimento readback failed.");
            }
        }

        using (var inbox = await ReadSuccessJsonAsync(
            client,
            "notifications",
            "/notifications/inbox?limit=10",
            cancellationToken))
        {
            var notifications = inbox.RootElement.GetProperty("items").EnumerateArray().ToArray();
            if (notifications.Length != 3
                || notifications.Count(notification =>
                    notification.GetProperty("readAt").ValueKind == JsonValueKind.String) != 1
                || notifications.Any(notification =>
                    notification.GetProperty("companyId").GetGuid() == receipt.GamaCompanyId)
                || notifications.Any(notification =>
                    !IsKnownNotificationAction(notification.GetProperty("action"))))
            {
                throw new InvalidOperationException("E2E notifications readback failed.");
            }
        }

        await RequireCapabilityDeniedAsync(
            client,
            "denials",
            $"/foundation/companies/{receipt.GamaCompanyId}/employees",
            cancellationToken);
        await RequireCapabilityDeniedAsync(
            client,
            "denials",
            $"/paguai/requests?companyId={receipt.GamaCompanyId}",
            cancellationToken);
    }

    private static async Task VerifyBeatrizReadbackAsync(
        HttpClient client,
        E2eDatasetReceipt receipt,
        CancellationToken cancellationToken)
    {
        using (var employees = await ReadSuccessJsonAsync(
            client,
            "employees",
            $"/foundation/companies/{receipt.AlfaCompanyId}/employees",
            cancellationToken))
        {
            var names = employees.RootElement
                .EnumerateArray()
                .Select(employee => employee.GetProperty("displayName").GetString())
                .OrderBy(name => name, StringComparer.Ordinal);
            if (!names.SequenceEqual(["Beatriz Costa", "Maria Silva"]))
            {
                throw new InvalidOperationException("E2E Beatriz employee readback failed.");
            }
        }

        using var atendimento = await ReadSuccessJsonAsync(
            client,
            "atendimento",
            $"/atendimento/cards?companyId={receipt.AlfaCompanyId}",
            cancellationToken);
        var cards = atendimento.RootElement.EnumerateArray().ToArray();
        if (cards.Length != 3
            || cards.Any(card =>
                card.GetProperty("companyId").GetGuid() != receipt.AlfaCompanyId)
            || !cards
                .Select(card => card.GetProperty("column").GetString())
                .OrderBy(column => column, StringComparer.Ordinal)
                .SequenceEqual(["concluido", "em_andamento", "novo"]))
        {
            throw new InvalidOperationException("E2E Beatriz Atendimento readback failed.");
        }
    }

    private static async Task VerifyJoaoReadbackAsync(
        HttpClient client,
        E2eDatasetReceipt receipt,
        CancellationToken cancellationToken)
    {
        using var paguai = await ReadSuccessJsonAsync(
            client,
            "paguai",
            $"/paguai/requests?companyId={receipt.BetaCompanyId}",
            cancellationToken);
        var drafts = paguai.RootElement.EnumerateArray().ToArray();
        if (drafts.Length != 1
            || drafts[0].GetProperty("companyId").GetGuid() != receipt.BetaCompanyId
            || !string.Equals(
                drafts[0].GetProperty("title").GetString(),
                "Hospedagem em nuvem",
                StringComparison.Ordinal)
            || drafts[0].GetProperty("amount").ValueKind != JsonValueKind.String
            || !string.Equals(
                drafts[0].GetProperty("amount").GetString(),
                "2340.75",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("E2E João Paguai readback failed.");
        }
    }

    private static async Task VerifyClaraReadbackAsync(
        HttpClient client,
        E2eDatasetReceipt receipt,
        CancellationToken cancellationToken)
    {
        using (var atendimento = await ReadSuccessJsonAsync(
            client,
            "atendimento",
            $"/atendimento/cards?companyId={receipt.GamaCompanyId}",
            cancellationToken))
        {
            var cards = atendimento.RootElement.EnumerateArray().ToArray();
            if (cards.Length != 1
                || cards[0].GetProperty("companyId").GetGuid() != receipt.GamaCompanyId
                || !string.Equals(
                    cards[0].GetProperty("title").GetString(),
                    "Caso restrito",
                    StringComparison.Ordinal)
                || !string.Equals(
                    cards[0].GetProperty("column").GetString(),
                    "em_andamento",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("E2E Clara Atendimento readback failed.");
            }
        }

        using var inbox = await ReadSuccessJsonAsync(
            client,
            "notifications",
            "/notifications/inbox?limit=10",
            cancellationToken);
        var notifications = inbox.RootElement.GetProperty("items").EnumerateArray().ToArray();
        if (notifications.Length != 1
            || notifications[0].GetProperty("companyId").GetGuid() != receipt.GamaCompanyId
            || notifications[0].GetProperty("readAt").ValueKind != JsonValueKind.Null
            || !IsKnownNotificationAction(notifications[0].GetProperty("action"))
            || !string.Equals(
                notifications[0].GetProperty("action").GetProperty("href").GetString(),
                "/atendimento",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("E2E Clara notifications readback failed.");
        }
    }

    private static bool IsKnownNotificationAction(JsonElement action)
    {
        if (action.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var href = action.GetProperty("href").GetString();
        return string.Equals(href, "/paguai", StringComparison.Ordinal)
            || string.Equals(href, "/atendimento", StringComparison.Ordinal);
    }

    /// <summary>
    /// Applies a per-request budget to one readback stage so a slow early call cannot
    /// silently starve later stages, and names the stage in the failure when it is exceeded.
    /// Cancellation requested by the caller is propagated unchanged for clean-cancellation handling.
    /// </summary>
    private static async Task<T> RunReadbackStageAsync<T>(
        string stage,
        Func<CancellationToken, Task<T>> stageOperation,
        CancellationToken cancellationToken)
    {
        using var stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stageCancellation.CancelAfter(ReadbackStageTimeout);
        try
        {
            return await stageOperation(stageCancellation.Token);
        }
        catch (OperationCanceledException exception) when (
            stageCancellation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"The E2E readback stage '{stage}' exceeded its per-request budget of {ReadbackStageTimeout.TotalSeconds:0}s.",
                exception);
        }
    }

    private static Task<JsonDocument> ReadSuccessJsonAsync(
        HttpClient client,
        string stage,
        string requestUri,
        CancellationToken cancellationToken) =>
        RunReadbackStageAsync(
            stage,
            async token =>
            {
                using var response = await client.GetAsync(requestUri, token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("E2E HTTP readback returned an unexpected status.");
                }

                await using var content = await response.Content.ReadAsStreamAsync(token);
                return await JsonDocument.ParseAsync(content, cancellationToken: token);
            },
            cancellationToken);

    private static Task RequireCapabilityDeniedAsync(
        HttpClient client,
        string stage,
        string requestUri,
        CancellationToken cancellationToken) =>
        RunReadbackStageAsync(
            stage,
            async token =>
            {
                using var response = await client.GetAsync(requestUri, token);
                if (response.StatusCode != HttpStatusCode.Forbidden
                    || !string.Equals(
                        await response.Content.ReadAsStringAsync(token),
                        "{\"code\":\"capability_denied\"}",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("E2E authorization readback failed.");
                }

                return true;
            },
            cancellationToken);

    private static async Task<Exception> DisposeStartFailureAsync(
        E2eFixtureFactoryContext? context,
        HttpClient? client,
        Exception primaryFailure)
    {
        if (context is null)
        {
            return primaryFailure;
        }

        try
        {
            await FixtureCleanup.CompleteAsync(
                primaryFailure,
                () => DisposeClientAsync(client),
                context.Factory.DisposeAsync);
        }
        catch (Exception combinedFailure)
        {
            return combinedFailure;
        }

        return primaryFailure;
    }

    private async Task DisposeCoreAsync()
    {
        await FixtureCleanup.CompleteAsync(
            primaryFailure: null,
            () => DisposeClientAsync(_client),
            _factory.DisposeAsync);
    }

    private static ValueTask DisposeClientAsync(HttpClient? client)
    {
        client?.Dispose();
        return ValueTask.CompletedTask;
    }
}
