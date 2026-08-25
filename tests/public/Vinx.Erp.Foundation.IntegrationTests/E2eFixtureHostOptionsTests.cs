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
    public void TryParse_accepts_only_an_exact_allowlisted_profile_pair(string profileName)
    {
        Assert.True(
            E2eFixtureHostOptions.TryParse(["--profile", profileName], out var profile));
        Assert.Equal(profileName, profile);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void TryParse_rejects_missing_duplicate_extra_or_unknown_arguments(string[] arguments)
    {
        Assert.False(E2eFixtureHostOptions.TryParse(arguments, out var profile));
        Assert.Empty(profile);
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

    [Theory]
    [InlineData(E2eFixtureHostFailurePhase.FixtureStart, "fixture_start_failed")]
    [InlineData(E2eFixtureHostFailurePhase.Seed, "seed_failed")]
    [InlineData(E2eFixtureHostFailurePhase.HostStart, "host_start_failed")]
    [InlineData(E2eFixtureHostFailurePhase.Readback, "readback_failed")]
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
}
