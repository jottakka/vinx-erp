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
                    var configuration = CreateFixtureAgentConfiguration(request.AgentMode);
                    var singleHost = await E2eFixtureHost.StartAsync(
                        profile,
                        shutdown.Token,
                        configuration.Model,
                        configuration.Options,
                        configuration.Storage);
                    host = singleHost;
                    if (!shutdown.IsCancellationRequested)
                    {
                        await output.WriteSingleReadinessAsync(singleHost.Origin);
                    }

                    break;
                }
            case E2eFixtureHostMode.Shared:
                {
                    var configuration = CreateFixtureAgentConfiguration(request.AgentMode);
                    var sharedHost = await SharedE2eFixtureHost.StartAsync(
                        shutdown.Token,
                    configuration.Model,
                    configuration.Options,
                    configuration.Storage);
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

static FixtureAgentConfiguration CreateFixtureAgentConfiguration(E2eFixtureAgentMode mode) => mode switch
{
    E2eFixtureAgentMode.Deterministic => new(
        new DeterministicE2eAgentModel(),
        new AgentRunOptions
        {
            Provider = AgentRunOptions.OpenAiCompatibleProvider,
            Endpoint = new Uri("https://agent-provider.example/v1"),
            Model = "test-agent-model",
            Vision = new AgentRunVisionOptions
            {
                Provider = AgentRunOptions.OpenAiCompatibleProvider,
                Endpoint = new Uri("https://agent-provider.example/v1"),
                Model = "test-agent-model",
                ApprovedHosts = ["agent-provider.example"],
                AllowedClassifications = ["agentic_attachment"],
            },
        },
        new E2eFixtureAttachmentStorage()),
    E2eFixtureAgentMode.Real => new(
        null,
        new AgentRunOptions
        {
            Provider = AgentRunOptions.OpenAiCompatibleProvider,
            Endpoint = new Uri("https://opencode.ai/zen/go/v1"),
            Model = "deepseek-v4-flash-vision-exp",
            ApprovedHosts = ["opencode.ai"],
        },
        new E2eFixtureAttachmentStorage()),
    _ => throw new InvalidOperationException("The E2E agent mode was invalid."),
};

sealed record FixtureAgentConfiguration(
    IAgentModel? Model,
    AgentRunOptions Options,
    E2eFixtureAttachmentStorage Storage);
