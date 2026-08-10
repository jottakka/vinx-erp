using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests.Observability;

public sealed class LoggingInfrastructureTests
{
    [Fact]
    public void AddVinxLogging_is_idempotent_and_tracks_trace_and_span_ids()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddVinxLogging();
        builder.Logging.AddVinxLogging();

        using var host = builder.Build();
        var services = host.Services;

        Assert.Single(services.GetServices<ILoggerProvider>().OfType<ConsoleLoggerProvider>());
        Assert.Equal("json", services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName);

        var jsonOptions = services.GetRequiredService<IOptionsMonitor<JsonConsoleFormatterOptions>>().CurrentValue;
        Assert.Equal("O", jsonOptions.TimestampFormat);
        Assert.True(jsonOptions.UseUtcTimestamp);
        Assert.True(jsonOptions.IncludeScopes);

        var activityTracking = services.GetRequiredService<IOptions<LoggerFactoryOptions>>().Value.ActivityTrackingOptions;
        Assert.Equal(ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId, activityTracking);
        Assert.False(activityTracking.HasFlag(ActivityTrackingOptions.Tags));
        Assert.False(activityTracking.HasFlag(ActivityTrackingOptions.Baggage));
    }

    [Fact]
    public void AddVinxLogging_filters_routine_http_diagnostics()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddVinxLogging();

        using var host = builder.Build();
        var diagnosticsLogger = host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");

        Assert.False(diagnosticsLogger.IsEnabled(LogLevel.Information));
    }

    [Fact]
    public async Task RequestCorrelation_isolates_concurrent_scopes()
    {
        var capturedLogs = new CapturingLoggerProvider();
        using var serviceProvider = CreateServiceProvider(capturedLogs);
        var logger = serviceProvider.GetRequiredService<ILogger<LoggingInfrastructureTests>>();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;

        var app = new ApplicationBuilder(serviceProvider);
        app.UseVinxRequestCorrelation();
        app.Run(async context =>
        {
            if (Interlocked.Increment(ref requests) == 2)
            {
                arrived.SetResult();
            }

            await arrived.Task;
            logger.LogInformation("request {RequestPath}", context.Request.Path);
        });
        var pipeline = app.Build();

        var first = InvokeWithActivityAsync(pipeline, "/first");
        var second = InvokeWithActivityAsync(pipeline, "/second");
        await Task.WhenAll(first, second);

        var requestLogs = capturedLogs.Entries.Where(entry => entry.Message.StartsWith("request ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, requestLogs.Length);

        var correlationIds = requestLogs.Select(GetCorrelationId).ToArray();
        Assert.All(correlationIds, correlationId => Assert.NotEqual(Guid.Empty, correlationId));
        Assert.Equal(2, correlationIds.Distinct().Count());
        Assert.All(requestLogs, entry => Assert.Single(entry.Scopes.OfType<IReadOnlyDictionary<string, object?>>()));
    }

    [Fact]
    public async Task RequestCorrelation_disposes_scope_when_downstream_fails()
    {
        var capturedLogs = new CapturingLoggerProvider();
        using var serviceProvider = CreateServiceProvider(capturedLogs);
        var logger = serviceProvider.GetRequiredService<ILogger<LoggingInfrastructureTests>>();

        var app = new ApplicationBuilder(serviceProvider);
        app.UseVinxRequestCorrelation();
        app.Run(_ => throw new InvalidOperationException("downstream failure"));
        var pipeline = app.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline(new DefaultHttpContext()));
        logger.LogInformation("after failure");

        var afterFailure = Assert.Single(capturedLogs.Entries, entry => entry.Message == "after failure");
        Assert.Empty(afterFailure.Scopes.OfType<IReadOnlyDictionary<string, object?>>());
    }

    private static ServiceProvider CreateServiceProvider(CapturingLoggerProvider capturedLogs)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.AddVinxLogging();
            logging.AddProvider(capturedLogs);
        });
        return services.BuildServiceProvider();
    }

    private static async Task InvokeWithActivityAsync(RequestDelegate pipeline, string path)
    {
        using var activity = new Activity("request").Start();
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        await pipeline(context);
    }

    private static Guid GetCorrelationId(CapturedLogEntry entry)
    {
        var scope = Assert.Single(entry.Scopes.OfType<IReadOnlyDictionary<string, object?>>());
        Assert.Equal(new[] { "CorrelationId" }, scope.Keys);
        return Assert.IsType<Guid>(scope["CorrelationId"]);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly ConcurrentQueue<CapturedLogEntry> entries = new();
        private IExternalScopeProvider scopeProvider = new LoggerExternalScopeProvider();

        public IReadOnlyCollection<CapturedLogEntry> Entries => entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        public void SetScopeProvider(IExternalScopeProvider provider) => scopeProvider = provider;

        private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider.scopeProvider.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var scopes = new List<object?>();
                provider.scopeProvider.ForEachScope(static (scope, recordedScopes) => recordedScopes.Add(scope), scopes);
                provider.entries.Enqueue(new CapturedLogEntry(formatter(state, exception), scopes));
            }
        }
    }

    private sealed record CapturedLogEntry(string Message, IReadOnlyCollection<object?> Scopes);
}
