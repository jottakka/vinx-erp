using System.Runtime.InteropServices;
using Vinx.Erp.AI;
using Vinx.Erp.ApiHost.Agent;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.AI.Providers.OpenAiCompatible;

return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    if (!E2eFixtureHostOptions.TryParse(arguments, out var request) || request is null)
    {
        return 2;
    }

    if (request.AgentMode == E2eFixtureAgentMode.Real
        && !OpenAiCompatibleChatClient.HasServerCredential(Environment.GetEnvironmentVariable))
    {
        return 2;
    }

    using var output = E2eFixtureHostOutput.SuppressHostConsole();
    using var shutdown = new CancellationTokenSource();
    using var interrupt = RegisterShutdown(PosixSignal.SIGINT, shutdown);
    using var terminate = RegisterShutdown(PosixSignal.SIGTERM, shutdown);
    IAsyncDisposable? host = null;
    Exception? primaryFailure = null;

    try
    {
        switch (request.Mode)
        {
            case E2eFixtureHostMode.Single:
                {
                    var profile = request.Profile
                        ?? throw new InvalidOperationException("The single E2E profile was missing.");
                    var singleHost = await E2eFixtureHost.StartAsync(profile, shutdown.Token);
                    host = singleHost;
                    if (!shutdown.IsCancellationRequested)
                    {
                        await output.WriteSingleReadinessAsync(singleHost.Origin);
                    }

                    break;
            }
            case E2eFixtureHostMode.Shared:
                {
                    var (testAgentModel, testAgentRunOptions) = request.AgentMode switch
                    {
                        E2eFixtureAgentMode.Deterministic => (
                            (IAgentModel)new DeterministicE2eAgentModel(),
                            new AgentRunOptions
                            {
                                Provider = AgentRunOptions.OpenAiCompatibleProvider,
                                Endpoint = new Uri("https://agent-provider.example/v1"),
                                Model = "test-agent-model",
                            }),
                        E2eFixtureAgentMode.Real => (
                            (IAgentModel?)null,
                            new AgentRunOptions
                            {
                                Provider = AgentRunOptions.OpenAiCompatibleProvider,
                                Endpoint = new Uri("https://opencode.ai/zen/go/v1"),
                                Model = "deepseek-v4-flash-vision-exp",
                                ApprovedHosts = ["opencode.ai"],
                            }),
                        _ => throw new InvalidOperationException("The E2E agent mode was invalid."),
                    };
                    var sharedHost = await SharedE2eFixtureHost.StartAsync(
                        shutdown.Token,
                        testAgentModel,
                        testAgentRunOptions);
                    host = sharedHost;
                    if (!shutdown.IsCancellationRequested)
                    {
                        await output.WriteSharedReadinessAsync(new E2eSharedFixtureReadiness(
                            sharedHost.MariaOrigin,
                            sharedHost.ClaraOrigin,
                            sharedHost.DatabaseHost,
                            sharedHost.DatabasePort,
                            sharedHost.DatabaseName,
                            sharedHost.DatabaseUser,
                            sharedHost.DatabasePasswordFilePath));
                    }

                    break;
                }
            default:
                throw new InvalidOperationException("The E2E host mode was invalid.");
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
    }
    catch (Exception exception)
    {
        primaryFailure = exception;
    }

    Exception? cleanupFailure = null;
    if (host is not null)
    {
        try
        {
            await E2eFixtureHostProcessPolicy.DisposeWithOneRetryAsync(host);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }
    }

    if (cleanupFailure is not null)
    {
        await output.WriteFailureAsync(E2eFixtureHostFailurePhase.Cleanup);
        return 1;
    }

    if (primaryFailure is null
        || E2eFixtureHostProcessPolicy.IsCleanCancellation(
            primaryFailure,
            shutdown.IsCancellationRequested))
    {
        return 0;
    }

    if (primaryFailure is E2eFixtureHostException fixtureHostFailure)
    {
        await output.WriteFailureAsync(fixtureHostFailure.Phase);
        return 1;
    }

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
