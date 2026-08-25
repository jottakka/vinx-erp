using System.Runtime.InteropServices;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

if (!E2eFixtureHostOptions.TryParse(args, out var profile))
{
    return 2;
}

using var output = E2eFixtureHostOutput.SuppressHostConsole();
using var shutdown = new CancellationTokenSource();
try
{
    using var interrupt = RegisterShutdown(PosixSignal.SIGINT, shutdown);
    using var terminate = RegisterShutdown(PosixSignal.SIGTERM, shutdown);
    await using var host = await E2eFixtureHost.StartAsync(profile, shutdown.Token);
    if (shutdown.IsCancellationRequested)
    {
        return 0;
    }

    await output.WriteReadinessAsync(host.Origin);

    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
    }

    return 0;
}
catch (Exception exception) when (E2eFixtureHostProcessPolicy.IsCleanCancellation(
    exception,
    shutdown.IsCancellationRequested))
{
    return 0;
}
catch (E2eFixtureHostException exception)
{
    await output.WriteFailureAsync(exception.Phase);
    return 1;
}
catch
{
    await output.WriteFailureAsync(E2eFixtureHostFailurePhase.HostStart);
    return 1;
}

static PosixSignalRegistration RegisterShutdown(
    PosixSignal signal,
    CancellationTokenSource shutdown) => PosixSignalRegistration.Create(
    signal,
    context =>
    {
        context.Cancel = true;
        shutdown.Cancel();
    });
