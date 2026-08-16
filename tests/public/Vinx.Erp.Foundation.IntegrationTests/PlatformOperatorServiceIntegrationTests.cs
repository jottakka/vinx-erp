using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Platform;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PlatformOperatorServiceIntegrationTests
{
    [Fact]
    public async Task Evaluate_uses_only_the_trusted_actor_context_and_never_audits_a_read()
    {
        await using var fixture = await StartFixtureAsync();
        var operatorIdentity = await SeedPlatformOperatorAsync(fixture);
        await using var runtime = fixture.CreateRuntimeDbContext();
        await runtime.Database.OpenConnectionAsync();
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {Guid.NewGuid().ToString()}, false)"
        );
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {Guid.NewGuid().ToString()}, false)"
        );
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.authorized_company_ids', {Guid.NewGuid().ToString()}, false)"
        );
        var service = CreateAccessService(runtime);

        var discovery = await service.EvaluateAsync(
            new ActorContext(
                operatorIdentity.UserId,
                Guid.NewGuid(),
                operatorIdentity.ExternalIdentityId
            ),
            CancellationToken.None
        );
        var requirement = await service.RequireAsync(
            new ActorContext(
                operatorIdentity.UserId,
                Guid.NewGuid(),
                operatorIdentity.ExternalIdentityId
            ),
            CancellationToken.None
        );

        Assert.Equal(PlatformOperatorAccessState.Allowed, discovery.State);
        Assert.Equal(PlatformOperatorAccessState.Allowed, requirement.State);
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Evaluate_denies_a_nonoperator_without_audit_and_require_records_one_global_denial()
    {
        await using var fixture = await StartFixtureAsync();
        await using var runtime = fixture.CreateRuntimeDbContext();
        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());
        var service = CreateAccessService(runtime);

        var discovery = await service.EvaluateAsync(actor, CancellationToken.None);
        var requirement = await service.RequireAsync(actor, CancellationToken.None);

        Assert.Equal(PlatformOperatorAccessState.Denied, discovery.State);
        Assert.Equal(PlatformOperatorAccessState.Denied, requirement.State);
        await using var administrator = fixture.CreateAdministratorDbContext();
        var denial = Assert.Single(await administrator.AuditEvents.ToListAsync());
        Assert.Equal(actor.UserId, denial.ActorUserId);
        Assert.Equal(actor.CorrelationId, denial.CorrelationId);
        Assert.Equal(AuditScope.Global, denial.Scope);
        Assert.Null(denial.RequestedCompanyId);
        Assert.Equal(AuditSource.Web, denial.Source);
        Assert.Equal("platform.operator.required", denial.Action);
        Assert.Equal("platform_operator", denial.ResourceType);
        Assert.Equal(AuditOutcome.Denied, denial.Outcome);
        Assert.Equal("platform_operator_required", denial.Reason);
        Assert.Equal("{}", denial.MetadataJson);
    }

    [Fact]
    public async Task Require_fails_closed_when_denial_audit_is_unavailable()
    {
        await using var fixture = await StartFixtureAsync();
        await RevokeAuditAppendAsync(fixture);
        await using var runtime = fixture.CreateRuntimeDbContext();
        var service = CreateAccessService(runtime);

        var result = await service.RequireAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None
        );

        Assert.Equal(PlatformOperatorAccessState.Unavailable, result.State);
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Evaluate_returns_unavailable_and_logs_one_safe_failure_when_function_cannot_run()
    {
        await using var fixture = await StartFixtureAsync();
        await RevokeOperatorFunctionAsync(fixture);
        await using var runtime = fixture.CreateRuntimeDbContext();
        var logs = new PlatformOperatorLogCapture();
        var service = CreateAccessService(runtime, logs);
        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());

        var result = await service.EvaluateAsync(actor, CancellationToken.None);

        Assert.Equal(PlatformOperatorAccessState.Unavailable, result.State);
        var log = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.NotNull(log.Exception);
        Assert.Equal(
            "Platform operator access {Operation} {Module} {Result} {Reason}",
            log.Template
        );
        Assert.Equal("platform_operator_access", log.Properties["Operation"]);
        Assert.Equal("foundation", log.Properties["Module"]);
        Assert.Equal("unavailable", log.Properties["Result"]);
        Assert.Equal("operator_evaluation_failed", log.Properties["Reason"]);
        Assert.Equal(
            ["Module", "Operation", "Reason", "Result"],
            log.Properties.Keys.OrderBy(key => key)
        );
        Assert.DoesNotContain(
            actor.UserId.ToString(),
            log.Properties.Values.Select(value => value?.ToString())
        );
        Assert.DoesNotContain(
            actor.CorrelationId.ToString(),
            log.Properties.Values.Select(value => value?.ToString())
        );
        Assert.DoesNotContain(
            actor.UserId.ToString(),
            log.Exception.ToString(),
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            actor.CorrelationId.ToString(),
            log.Exception.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task Evaluate_propagates_caller_cancellation()
    {
        await using var fixture = await StartFixtureAsync();
        await using var runtime = fixture.CreateRuntimeDbContext();
        var service = CreateAccessService(runtime);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EvaluateAsync(
                new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
                cancellation.Token
            )
        );
    }

    [Fact]
    public async Task Bootstrap_maps_exact_database_statuses_without_external_calls()
    {
        await using var fixture = await StartFixtureAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        var service = CreateBootstrapService(migrator);
        var command = NewBootstrapCommand();

        var created = await service.ExecuteAsync(command, CancellationToken.None);
        var replay = await service.ExecuteAsync(command, CancellationToken.None);
        var conflict = await service.ExecuteAsync(
            command with
            {
                Subject = "different-subject",
            },
            CancellationToken.None
        );

        Assert.Equal(PlatformOperatorBootstrapState.Created, created);
        Assert.Equal(PlatformOperatorBootstrapState.AlreadyConfigured, replay);
        Assert.Equal(PlatformOperatorBootstrapState.Conflict, conflict);
    }

    [Fact]
    public async Task Bootstrap_fails_closed_and_logs_no_command_or_database_data()
    {
        await using var fixture = await StartFixtureAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        var logs = new PlatformOperatorBootstrapLogCapture();
        var service = CreateBootstrapService(migrator, logs);
        var command = new PlatformOperatorBootstrapCommand(
            "leak-subject",
            "leak-issuer",
            "leak-display-name",
            "leak-provider@example.test",
            Guid.NewGuid()
        );

        var result = await service.ExecuteAsync(
            command with
            {
                Subject = " ",
            },
            CancellationToken.None
        );

        Assert.Equal(PlatformOperatorBootstrapState.Unavailable, result);
        var log = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.NotNull(log.Exception);
        Assert.Equal(
            "Platform operator bootstrap {Operation} {Module} {Result} {Reason}",
            log.Template
        );
        Assert.Equal("platform_operator_bootstrap", log.Properties["Operation"]);
        Assert.Equal("foundation", log.Properties["Module"]);
        Assert.Equal("unavailable", log.Properties["Result"]);
        Assert.Equal("bootstrap_execution_failed", log.Properties["Reason"]);
        Assert.Equal(
            ["Module", "Operation", "Reason", "Result"],
            log.Properties.Keys.OrderBy(key => key)
        );
        Assert.DoesNotContain(
            command.Subject,
            log.Properties.Values.Select(value => value?.ToString())
        );
        Assert.DoesNotContain(
            command.Issuer,
            log.Properties.Values.Select(value => value?.ToString())
        );
        Assert.DoesNotContain(
            command.DisplayName,
            log.Properties.Values.Select(value => value?.ToString())
        );
        Assert.DoesNotContain(
            command.ProviderEmail,
            log.Properties.Values.Select(value => value?.ToString())
        );
        Assert.DoesNotContain(
            command.CorrelationId.ToString(),
            log.Properties.Values.Select(value => value?.ToString())
        );
        Assert.DoesNotContain(command.Subject, log.Exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(command.Issuer, log.Exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            command.DisplayName,
            log.Exception.ToString(),
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            command.ProviderEmail,
            log.Exception.ToString(),
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            command.CorrelationId.ToString(),
            log.Exception.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task Bootstrap_propagates_caller_cancellation()
    {
        await using var fixture = await StartFixtureAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        var service = CreateBootstrapService(migrator);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExecuteAsync(NewBootstrapCommand(), cancellation.Token)
        );
    }

    private static async Task<FoundationPostgresFixture> StartFixtureAsync()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static PlatformOperatorAccessService CreateAccessService(
        FoundationDbContext database,
        ILogger<PlatformOperatorAccessService>? logger = null
    ) =>
        new(
            database,
            new AuditAppender(database),
            logger ?? NullLogger<PlatformOperatorAccessService>.Instance
        );

    private static PlatformOperatorBootstrapService CreateBootstrapService(
        FoundationDbContext database,
        ILogger<PlatformOperatorBootstrapService>? logger = null
    ) => new(database, logger ?? NullLogger<PlatformOperatorBootstrapService>.Instance);

    private static PlatformOperatorBootstrapCommand NewBootstrapCommand() =>
        new(
            "subject-operator-1",
            "https://issuer.bootstrap.test",
            "Deployment Operator",
            "operator@bootstrap.test",
            Guid.NewGuid()
        );

    private static async Task<SeededPlatformOperator> SeedPlatformOperatorAsync(
        FoundationPostgresFixture fixture
    )
    {
        var userId = Guid.NewGuid();
        await using (var administrator = fixture.CreateAdministratorDbContext())
        {
            administrator.Users.Add(
                new User(userId, "Platform Operator", $"{userId:N}@example.test")
            );
            await administrator.SaveChangesAsync();
        }

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        var identityId = Guid.NewGuid();
        await using (
            var identity = new NpgsqlCommand(
                """
                INSERT INTO foundation.external_identities (id, user_id, issuer, subject, created_at)
                VALUES (@identity_id, @user_id, 'https://issuer.platform.test', @subject, clock_timestamp())
                """,
                connection
            )
        )
        {
            identity.Parameters.AddWithValue("identity_id", identityId);
            identity.Parameters.AddWithValue("user_id", userId);
            identity.Parameters.AddWithValue("subject", $"operator-{userId:N}");
            await identity.ExecuteNonQueryAsync();
        }

        await using var operatorRow = new NpgsqlCommand(
            "INSERT INTO foundation.platform_operators (singleton, external_identity_id) VALUES (1, @identity_id)",
            connection
        );
        operatorRow.Parameters.AddWithValue("identity_id", identityId);
        await operatorRow.ExecuteNonQueryAsync();
        return new SeededPlatformOperator(userId, identityId);
    }

    private sealed record SeededPlatformOperator(Guid UserId, Guid ExternalIdentityId);

    private static async Task RevokeOperatorFunctionAsync(FoundationPostgresFixture fixture)
    {
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION foundation.current_actor_is_platform_operator() FROM vinx_runtime;",
            connection
        );
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RevokeAuditAppendAsync(FoundationPostgresFixture fixture)
    {
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb) FROM vinx_runtime;",
            connection
        );
        await command.ExecuteNonQueryAsync();
    }

    private sealed record CapturedLog(
        LogLevel Level,
        Exception? Exception,
        string Template,
        IReadOnlyDictionary<string, object?> Properties
    );

    private sealed class PlatformOperatorLogCapture : ILogger<PlatformOperatorAccessService>
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
            var template =
                values["{OriginalFormat}"]?.ToString()
                ?? throw new InvalidOperationException("The structured log template was missing.");
            values.Remove("{OriginalFormat}");
            Entries.Add(new CapturedLog(logLevel, exception, template, values));
        }
    }

    private sealed class PlatformOperatorBootstrapLogCapture
        : ILogger<PlatformOperatorBootstrapService>
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
            var template =
                values["{OriginalFormat}"]?.ToString()
                ?? throw new InvalidOperationException("The structured log template was missing.");
            values.Remove("{OriginalFormat}");
            Entries.Add(new CapturedLog(logLevel, exception, template, values));
        }
    }
}
