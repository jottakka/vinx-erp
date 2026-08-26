using System.Globalization;
using System.Runtime.InteropServices;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

[Collection(RealE2eFixtureHostCollection.Name)]
public sealed class E2eFixtureHostOptionsTests
{
    [Theory]
    [InlineData("maria")]
    [InlineData("beatriz")]
    [InlineData("joao")]
    [InlineData("clara")]
    public void TryParse_accepts_an_exact_allowlisted_single_profile(string profileName)
    {
        Assert.True(
            E2eFixtureHostOptions.TryParse(["--profile", profileName], out var request));
        Assert.Equal(new E2eFixtureHostRequest(E2eFixtureHostMode.Single, profileName), request);
    }

    [Fact]
    public void TryParse_accepts_only_the_literal_shared_maria_clara_pair()
    {
        Assert.True(
            E2eFixtureHostOptions.TryParse(["--profiles", "maria,clara"], out var request));
        Assert.Equal(new E2eFixtureHostRequest(E2eFixtureHostMode.Shared, null), request);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void TryParse_rejects_missing_duplicate_extra_or_unknown_arguments(string[] arguments)
    {
        Assert.False(E2eFixtureHostOptions.TryParse(arguments, out var request));
        Assert.Null(request);
    }

    [Fact]
    public async Task Public_output_suppresses_host_console_before_and_after_readiness()
    {
        var captured = await CaptureAsync(async output =>
        {
            await Console.Out.WriteLineAsync("host stdout must not escape");
            await Console.Error.WriteLineAsync("host stderr must not escape");
            await output.WriteReadinessAsync(new Uri("http://127.0.0.1:43210"));
            await Console.Out.WriteLineAsync("late host stdout must not escape");
            await Console.Error.WriteLineAsync("late host stderr must not escape");
        });

        Assert.Equal(
            $"E2E_API_ORIGIN=http://127.0.0.1:43210{Environment.NewLine}",
            captured.StandardOutput);
        Assert.Empty(captured.StandardError);
    }

    [Fact]
    public async Task Shared_public_output_writes_only_the_complete_sanitized_readiness_block()
    {
        var captured = await CaptureAsync(async output =>
        {
            await output.WriteSharedReadinessAsync(new E2eSharedFixtureReadiness(
                new Uri("http://127.0.0.1:43210"),
                new Uri("http://127.0.0.1:43211"),
                "127.0.0.1",
                43212,
                "vinx_fixture_a1b2c3",
                "postgres",
                "/tmp/vinx-e2e-check/postgres-password"));
        });

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                [
                    "E2E_API_ORIGIN_MARIA=http://127.0.0.1:43210",
                    "E2E_API_ORIGIN_CLARA=http://127.0.0.1:43211",
                    "E2E_DB_HOST=127.0.0.1",
                    "E2E_DB_PORT=43212",
                    "E2E_DB_DATABASE=vinx_fixture_a1b2c3",
                    "E2E_DB_USER=postgres",
                    "E2E_DB_PASSWORD_FILE=/tmp/vinx-e2e-check/postgres-password",
                    "E2E_SHARED_READY=1",
                    string.Empty,
                ]),
            captured.StandardOutput);
        Assert.Empty(captured.StandardError);
        Assert.DoesNotContain("Password=", captured.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-credential-value", captured.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shared_public_output_rejects_a_database_name_without_a_hex_suffix()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CaptureAsync(output =>
            output.WriteSharedReadinessAsync(new E2eSharedFixtureReadiness(
                new Uri("http://127.0.0.1:43210"),
                new Uri("http://127.0.0.1:43211"),
                "127.0.0.1",
                43212,
                "vinx_fixture_",
                "postgres",
                "/tmp/vinx-e2e-check/postgres-password"))));
    }

    [Fact]
    public async Task Credential_file_is_private_regular_without_a_trailing_newline_and_is_removed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var credentialFile = await E2eDatabaseCredentialFile.CreateAsync("fixture-credential-value");
        var filePath = credentialFile.Path;
        var directoryPath = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("The credential directory was missing.");

        try
        {
            Assert.True(Path.IsPathFullyQualified(filePath));
            Assert.Equal("fixture-credential-value", await File.ReadAllTextAsync(filePath));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(filePath));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(directoryPath));
            Assert.Null(new FileInfo(filePath).LinkTarget);
        }
        finally
        {
            await credentialFile.DisposeAsync();
        }

        Assert.False(File.Exists(filePath));
        Assert.False(Directory.Exists(directoryPath));
    }

    [Theory]
    [InlineData(E2eFixtureHostFailurePhase.FixtureStart, "fixture_start_failed")]
    [InlineData(E2eFixtureHostFailurePhase.Seed, "seed_failed")]
    [InlineData(E2eFixtureHostFailurePhase.HostStart, "host_start_failed")]
    [InlineData(E2eFixtureHostFailurePhase.Readback, "readback_failed")]
    [InlineData(E2eFixtureHostFailurePhase.CredentialExport, "credential_export_failed")]
    [InlineData(E2eFixtureHostFailurePhase.Cleanup, "cleanup_failed")]
    public async Task Public_output_maps_typed_failures_without_exception_details(
        E2eFixtureHostFailurePhase phase,
        string category)
    {
        var captured = await CaptureAsync(async output =>
        {
            await Console.Out.WriteLineAsync("host stdout must not escape");
            await Console.Error.WriteLineAsync("secret exception details must not escape");
            await output.WriteFailureAsync(phase);
        });

        Assert.Empty(captured.StandardOutput);
        Assert.Equal(
            $"E2E_HOST_FAILED={category}{Environment.NewLine}",
            captured.StandardError);
        Assert.DoesNotContain("secret", captured.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_policy_treats_only_clean_requested_cancellation_as_success()
    {
        var directCancellation = new OperationCanceledException();
        var typedCancellation = new E2eFixtureHostException(
            E2eFixtureHostFailurePhase.HostStart,
            new OperationCanceledException());
        var cleanupFailure = new E2eFixtureHostException(
            E2eFixtureHostFailurePhase.HostStart,
            new AggregateException(
                new OperationCanceledException(),
                new InvalidOperationException("cleanup failed")));

        Assert.True(E2eFixtureHostProcessPolicy.IsCleanCancellation(
            directCancellation,
            cancellationRequested: true));
        Assert.True(E2eFixtureHostProcessPolicy.IsCleanCancellation(
            typedCancellation,
            cancellationRequested: true));
        Assert.False(E2eFixtureHostProcessPolicy.IsCleanCancellation(
            typedCancellation,
            cancellationRequested: false));
        Assert.False(E2eFixtureHostProcessPolicy.IsCleanCancellation(
            cleanupFailure,
            cancellationRequested: true));
    }

    [Fact]
    public async Task Process_policy_retries_one_failed_cleanup_and_preserves_the_first_failure()
    {
        var firstFailure = new InvalidOperationException("first-cleanup-failure");
        var resource = new FailOnceAsyncDisposable(firstFailure);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            E2eFixtureHostProcessPolicy.DisposeWithOneRetryAsync(resource));

        Assert.Same(firstFailure, exception);
        Assert.Equal(2, resource.DisposeCalls);
    }

    [Fact]
    public void Runtime_supports_the_two_required_posix_signal_registrations()
    {
        using var interrupt = PosixSignalRegistration.Create(
            PosixSignal.SIGINT,
            context => context.Cancel = true);
        using var terminate = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM,
            context => context.Cancel = true);
    }

    [Fact]
    public void E2e_factory_content_root_resolves_the_existing_ApiHost_project()
    {
        var contentRoot = FoundationApiFactory.ResolveE2eContentRoot();

        Assert.True(Path.IsPathFullyQualified(contentRoot));
        Assert.True(Directory.Exists(contentRoot));
        Assert.True(File.Exists(Path.Combine(contentRoot, "Vinx.Erp.ApiHost.csproj")));
    }

    public static TheoryData<string[]> InvalidArguments =>
    [
        [],
        ["--profile"],
        ["maria"],
        ["--profile", "unknown"],
        ["--profile", "Maria"],
        ["--profile", "maria "],
        ["--profile", "maria", "--profile", "joao"],
        ["--profile", "maria", "--extra"],
        ["--extra", "--profile", "maria"],
        ["--profiles", "maria"],
        ["--profiles", "clara,maria"],
        ["--profiles", "maria, clara"],
        ["--profiles", "maria,clara,joao"],
        ["--profiles", "maria,clara", "--profile", "maria"],
    ];

    private static async Task<CapturedOutput> CaptureAsync(
        Func<E2eFixtureHostOutput, Task> action)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var standardOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var standardError = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(standardOutput);
            Console.SetError(standardError);
            using (var output = E2eFixtureHostOutput.SuppressHostConsole())
            {
                await action(output);
            }

            return new CapturedOutput(standardOutput.ToString(), standardError.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private sealed record CapturedOutput(string StandardOutput, string StandardError);

    private sealed class FailOnceAsyncDisposable(Exception firstFailure) : IAsyncDisposable
    {
        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls += 1;
            return DisposeCalls == 1
                ? ValueTask.FromException(firstFailure)
                : ValueTask.CompletedTask;
        }
    }
}
