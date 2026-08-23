extern alias platformbootstrap;

using System.Net;
using System.Text;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Npgsql;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Platform;
using Xunit;
using ExternalIdentityReprovisioningOptions = platformbootstrap::Vinx.Erp.PlatformBootstrap.ExternalIdentityReprovisioningOptions;
using ExternalIdentityReprovisioningRunner = platformbootstrap::Vinx.Erp.PlatformBootstrap.ExternalIdentityReprovisioningRunner;
using PlatformBootstrapCommand = platformbootstrap::PlatformBootstrapCommand;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class ExternalIdentityReprovisioningServiceTests
{
    private const string Issuer = "https://reprovisioning.supabase.test/auth/v1";
    private const string Audience = "authenticated";
    private const string OldSubject = "old-provider-subject";
    private const string NewSubject = "new-provider-subject";
    private const string RetainedEmail = "retained@example.test";

    [Fact]
    public async Task Reprovision_async_changes_the_exact_configured_identity_after_both_provider_preflights()
    {
        await using var fixture = await StartFixtureAsync();
        var seed = await SeedAsync(fixture);
        var handler = new ControlledHttpMessageHandler(subject =>
            subject == OldSubject
                ? Json(HttpStatusCode.NotFound, "old-provider-payload")
                : Json(HttpStatusCode.OK, UserJson(NewSubject, RetainedEmail))
        );
        await using var database = fixture.CreateMigratorDbContext();
        var service = CreateService(database, handler);

        var result = await service.ReprovisionAsync(NewCommand(seed), CancellationToken.None);

        Assert.Equal(ExternalIdentityReprovisioningState.Reprovisioned, result);
        Assert.Equal([OldSubject, NewSubject], handler.RequestedSubjects);
        var state = await ReadStateAsync(fixture, seed);
        Assert.Equal(NewSubject, state.Subject);
        Assert.Equal(1L, state.ReprovisioningAudits);
        Assert.DoesNotContain(RetainedEmail, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(OldSubject, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(NewSubject, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_passes_the_exact_options_to_the_service_and_maps_its_stable_outcome()
    {
        await using var fixture = await StartFixtureAsync();
        var seed = await SeedAsync(fixture);
        var handler = new ControlledHttpMessageHandler(subject =>
            subject == OldSubject
                ? Json(HttpStatusCode.NotFound, "old-provider-payload")
                : Json(HttpStatusCode.OK, UserJson(NewSubject, RetainedEmail))
        );
        await using var database = fixture.CreateMigratorDbContext();
        var runner = new ExternalIdentityReprovisioningRunner(
            CreateService(database, handler),
            Options.Create(
                new ExternalIdentityReprovisioningOptions
                {
                    UserId = seed.UserId.ToString(),
                    ExpectedExternalIdentityId = seed.IdentityId.ToString(),
                    ExpectedOldSubject = OldSubject,
                    NewSubject = NewSubject,
                }
            )
        );

        var result = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(ExternalIdentityReprovisioningState.Reprovisioned, result);
        Assert.Equal(("reprovisioned", 0), PlatformBootstrapCommand.MapReprovisioningOutcome(result));
        Assert.Equal([OldSubject, NewSubject], handler.RequestedSubjects);
        var state = await ReadStateAsync(fixture, seed);
        Assert.Equal(NewSubject, state.Subject);
        Assert.Equal(1L, state.ReprovisioningAudits);
    }

    [Fact]
    public void Logging_composition_keeps_only_the_sanitized_service_error_on_json_stderr()
    {
        var capture = new FilteredLogCaptureProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            PlatformBootstrapCommand.ConfigureLogging(logging);
            logging.AddProvider(capture);
        });
        using var serviceProvider = services.BuildServiceProvider();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        loggerFactory.CreateLogger<ExternalIdentityReprovisioningService>().LogError("safe failure");
        loggerFactory.CreateLogger<ExternalIdentityReprovisioningService>().LogWarning("ordinary warning");
        loggerFactory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogError("ordinary error");

        Assert.Equal(
            LogLevel.Error,
            serviceProvider.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.LogToStandardErrorThreshold
        );
        var entry = Assert.Single(capture.Entries);
        Assert.Equal(typeof(ExternalIdentityReprovisioningService).FullName, entry.Category);
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Fact]
    public void Command_classification_preserves_the_default_bootstrap_and_wolverine_grants_paths()
    {
        Assert.Equal("Bootstrap", PlatformBootstrapCommand.Classify([]).ToString());
        Assert.Equal("WolverineGrants", PlatformBootstrapCommand.Classify(["wolverine-grants"]).ToString());
    }

    [Theory]
    [MemberData(nameof(RejectedCases))]
    public async Task Reprovision_async_denies_or_reports_unavailable_without_mutating_when_preconditions_fail(
        ProviderCase providerCase,
        ExternalIdentityReprovisioningState expected)
    {
        await using var fixture = await StartFixtureAsync();
        var seed = await SeedAsync(fixture);
        var handler = new ControlledHttpMessageHandler(subject => ResponseFor(providerCase, subject));
        await using var database = fixture.CreateMigratorDbContext();
        var service = CreateService(database, handler);
        var command = NewCommand(seed) with
        {
            ExpectedOldSubject = providerCase is ProviderCase.TupleMismatch
                ? "different-configured-old-subject"
                : OldSubject,
        };

        var result = await service.ReprovisionAsync(command, CancellationToken.None);

        Assert.Equal(expected, result);
        var state = await ReadStateAsync(fixture, seed);
        Assert.Equal(OldSubject, state.Subject);
        Assert.Equal(0L, state.ReprovisioningAudits);
        Assert.DoesNotContain(RetainedEmail, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(OldSubject, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(NewSubject, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reprovision_async_logs_only_safe_outcome_when_the_database_exception_contains_sensitive_values()
    {
        await using var fixture = await StartFixtureAsync();
        var seed = await SeedAsync(fixture);
        var sensitiveValues = new[]
        {
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            Issuer,
            OldSubject,
            NewSubject,
            "33333333-3333-3333-3333-333333333333",
            RetainedEmail,
            "provider-payload-marker",
            "token-credential-marker",
        };
        var logger = new ExternalIdentityReprovisioningLogCapture();
        var handler = new ControlledHttpMessageHandler(subject =>
            subject == OldSubject
                ? Json(HttpStatusCode.NotFound, "old-provider-payload")
                : Json(HttpStatusCode.OK, UserJson(NewSubject, RetainedEmail))
        );
        await using var database = CreateMigratorDbContext(
            fixture,
            new ThrowingReprovisioningCommandInterceptor(
                new InvalidOperationException(string.Join("|", sensitiveValues))
            )
        );
        var service = CreateService(database, handler, logger);

        var result = await service.ReprovisionAsync(NewCommand(seed), CancellationToken.None);

        Assert.Equal(ExternalIdentityReprovisioningState.Unavailable, result);
        var log = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Equal(
            "External identity reprovisioning {Operation} {Module} {Result} {Reason}",
            log.Template
        );
        Assert.Equal("external_identity_reprovisioning", log.Properties["Operation"]);
        Assert.Equal("foundation", log.Properties["Module"]);
        Assert.Equal("unavailable", log.Properties["Result"]);
        Assert.Equal("reprovisioning_execution_failed", log.Properties["Reason"]);
        Assert.Equal(
            ["Module", "Operation", "Reason", "Result"],
            log.Properties.Keys.OrderBy(key => key)
        );
        Assert.Null(log.Exception);
        foreach (var sensitiveValue in sensitiveValues)
        {
            Assert.DoesNotContain(sensitiveValue, log.Formatted, StringComparison.Ordinal);
            Assert.DoesNotContain(
                sensitiveValue,
                log.Properties.Values.Select(value => value?.ToString()),
                StringComparer.Ordinal
            );
        }

        var state = await ReadStateAsync(fixture, seed);
        Assert.Equal(OldSubject, state.Subject);
        Assert.Equal(0L, state.ReprovisioningAudits);
    }

    public static TheoryData<ProviderCase, ExternalIdentityReprovisioningState> RejectedCases =>
        new()
        {
            { ProviderCase.OldStillPresent, ExternalIdentityReprovisioningState.ProviderNotDeleted },
            { ProviderCase.InvalidNew, ExternalIdentityReprovisioningState.ProviderRejected },
            { ProviderCase.UnconfirmedNew, ExternalIdentityReprovisioningState.ProviderRejected },
            { ProviderCase.BannedNew, ExternalIdentityReprovisioningState.ProviderRejected },
            { ProviderCase.AnonymousNew, ExternalIdentityReprovisioningState.ProviderRejected },
            { ProviderCase.WrongAudienceNew, ExternalIdentityReprovisioningState.ProviderRejected },
            { ProviderCase.EmailMismatch, ExternalIdentityReprovisioningState.EmailMismatch },
            { ProviderCase.TupleMismatch, ExternalIdentityReprovisioningState.TupleMismatch },
            { ProviderCase.ProviderUnavailable, ExternalIdentityReprovisioningState.Unavailable },
        };

    private static HttpResponseMessage ResponseFor(ProviderCase providerCase, string subject)
    {
        if (providerCase is ProviderCase.ProviderUnavailable)
        {
            return Json(HttpStatusCode.BadGateway, "provider-payload-must-not-escape");
        }

        if (subject != NewSubject)
        {
            return providerCase is ProviderCase.OldStillPresent
                ? Json(HttpStatusCode.OK, UserJson(subject, RetainedEmail))
                : Json(HttpStatusCode.NotFound, "old-provider-payload");
        }

        return providerCase switch
        {
            ProviderCase.InvalidNew => Json(HttpStatusCode.OK, "{\"id\":\"new-provider-subject\"}"),
            ProviderCase.UnconfirmedNew => Json(HttpStatusCode.OK, UserJson(NewSubject, RetainedEmail, confirmed: false)),
            ProviderCase.BannedNew => Json(HttpStatusCode.OK, UserJson(NewSubject, RetainedEmail, banned: true)),
            ProviderCase.AnonymousNew => Json(HttpStatusCode.OK, UserJson(NewSubject, RetainedEmail, anonymous: true)),
            ProviderCase.WrongAudienceNew => Json(HttpStatusCode.OK, UserJson(NewSubject, RetainedEmail, audience: "other")),
            ProviderCase.EmailMismatch => Json(HttpStatusCode.OK, UserJson(NewSubject, "other@example.test")),
            _ => Json(HttpStatusCode.OK, UserJson(NewSubject, RetainedEmail)),
        };
    }

    private static ExternalIdentityReprovisioningService CreateService(
        Persistence.FoundationDbContext database,
        HttpMessageHandler handler,
        ILogger<ExternalIdentityReprovisioningService>? logger = null) =>
        new(
            database,
            new SupabaseAdminUserVerifier(
                new HttpClient(handler),
                new SupabaseAuthOptions { Issuer = Issuer, Audience = Audience },
                new SupabaseAdminOptions { ServiceRoleKey = "test-service-role-key" }
            ),
            new SupabaseAuthOptions { Issuer = Issuer, Audience = Audience },
            logger ?? NullLogger<ExternalIdentityReprovisioningService>.Instance
        );

    private static Persistence.FoundationDbContext CreateMigratorDbContext(
        FoundationPostgresFixture fixture,
        IInterceptor interceptor) =>
        new(
            new DbContextOptionsBuilder<Persistence.FoundationDbContext>()
                .UseNpgsql(fixture.MigratorConnectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(interceptor)
                .Options
        );

    private static ExternalIdentityReprovisioningCommand NewCommand(Seed seed) =>
        new(seed.UserId, seed.IdentityId, OldSubject, NewSubject, Guid.NewGuid());

    private static async Task<FoundationPostgresFixture> StartFixtureAsync()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static async Task<Seed> SeedAsync(FoundationPostgresFixture fixture)
    {
        var seed = new Seed(Guid.NewGuid(), Guid.NewGuid());
        await using var administrator = fixture.CreateAdministratorDbContext();
        administrator.Users.Add(new User(seed.UserId, "Retained Person", RetainedEmail));
        administrator.ExternalIdentities.Add(
            new ExternalIdentity(seed.IdentityId, seed.UserId, Issuer, OldSubject)
        );
        await administrator.SaveChangesAsync();
        return seed;
    }

    private static async Task<State> ReadStateAsync(FoundationPostgresFixture fixture, Seed seed)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT subject,
                (SELECT count(*) FROM audit.audit_events WHERE action = 'identity.external.reprovision')
            FROM foundation.external_identities
            WHERE id = @identity_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("identity_id", seed.IdentityId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new State(reader.GetString(0), reader.GetInt64(1));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string UserJson(
        string subject,
        string email,
        string audience = Audience,
        bool confirmed = true,
        bool banned = false,
        bool anonymous = false) =>
        $$"""
        {"id":"{{subject}}","aud":"{{audience}}","email":"{{email}}","email_confirmed_at":{{(confirmed ? "\"2026-08-23T00:00:00Z\"" : "null")}},"banned_until":{{(banned ? "\"2026-08-24T00:00:00Z\"" : "null")}},"is_anonymous":{{anonymous.ToString().ToLowerInvariant()}},"identities":[{"identity_id":"identity","user_id":"{{subject}}","provider":"email"}]}
        """;

    public enum ProviderCase
    {
        OldStillPresent,
        InvalidNew,
        UnconfirmedNew,
        BannedNew,
        AnonymousNew,
        WrongAudienceNew,
        EmailMismatch,
        TupleMismatch,
        ProviderUnavailable,
    }

    private sealed record Seed(Guid UserId, Guid IdentityId);
    private sealed record State(string Subject, long ReprovisioningAudits);
    private sealed record CapturedLog(
        LogLevel Level,
        Exception? Exception,
        string Template,
        string Formatted,
        IReadOnlyDictionary<string, object?> Properties
    );

    private sealed class ExternalIdentityReprovisioningLogCapture
        : ILogger<ExternalIdentityReprovisioningService>
    {
        public List<CapturedLog> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var values = ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal
            );
            var template = values["{OriginalFormat}"]?.ToString()
                ?? throw new InvalidOperationException("The structured log template was missing.");
            values.Remove("{OriginalFormat}");
            Entries.Add(new CapturedLog(logLevel, exception, template, formatter(state, exception), values));
        }
    }

    private sealed class FilteredLogCaptureProvider : ILoggerProvider
    {
        public List<(string Category, LogLevel Level)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new FilteredLogCapture(Entries, categoryName);

        public void Dispose() { }
    }

    private sealed class FilteredLogCapture(List<(string Category, LogLevel Level)> entries, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => entries.Add((category, logLevel));
    }

    private sealed class ThrowingReprovisioningCommandInterceptor(Exception exception)
        : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            if (command.CommandText.Contains("reprovision_external_identity", StringComparison.Ordinal))
            {
                throw exception;
            }

            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }
    }

    private sealed class ControlledHttpMessageHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> RequestedSubjects { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var subject = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]);
            RequestedSubjects.Add(subject);
            return Task.FromResult(respond(subject));
        }
    }
}
