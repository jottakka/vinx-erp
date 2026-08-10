using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class InternalSessionAdmissionServiceTests
{
    [Fact]
    public async Task New_proof_creates_one_session_and_one_global_success_audit()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var userId = await SeedUserAsync(fixture);
        var proof = NewProof();
        var correlationId = Guid.NewGuid();

        await using var runtime = fixture.CreateRuntimeDbContext();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace));
        var service = new InternalSessionAdmissionService(
            runtime,
            new AuditAppender(runtime),
            loggerFactory.CreateLogger<InternalSessionAdmissionService>());

        var result = await service.AdmitAsync(
            userId,
            proof,
            AuditSource.Web,
            correlationId,
            CancellationToken.None);

        Assert.Equal(SessionAdmissionState.Admitted, result.State);

        await using var administrator = fixture.CreateAdministratorDbContext();
        var session = await administrator.UserSessions.SingleAsync();
        var audit = await administrator.AuditEvents.SingleAsync();
        Assert.Equal(userId, session.UserId);
        Assert.Equal(proof.Key.Issuer, session.Issuer);
        Assert.Equal(proof.ProviderSessionId, session.ProviderSessionId);
        Assert.Equal("identity_session_admitted", audit.Action);
        Assert.Equal(AuditScope.Global, audit.Scope);
        Assert.Equal(AuditOutcome.Success, audit.Outcome);
        Assert.Equal(AuditSource.Web, audit.Source);
        Assert.Equal(session.Id, audit.ResourceId);
        Assert.Equal("{}", audit.MetadataJson);
    }

    [Fact]
    public async Task Repeated_proof_is_active_without_another_audit()
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        var proof = NewProof();

        var first = await AdmitAsync(fixture, userId, proof, AuditSource.Web, Guid.NewGuid());
        var repeated = await AdmitAsync(fixture, userId, proof, AuditSource.Web, Guid.NewGuid());

        Assert.Equal(SessionAdmissionState.Admitted, first.Result.State);
        Assert.Equal(SessionAdmissionState.Active, repeated.Result.State);
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Single(await administrator.UserSessions.ToListAsync());
        Assert.Single(await administrator.AuditEvents.ToListAsync());
    }

    [Theory]
    [InlineData(-1, SessionAdmissionState.Denied)]
    [InlineData(0, SessionAdmissionState.Denied)]
    [InlineData(1, SessionAdmissionState.Admitted)]
    public async Task Barrier_requires_authentication_strictly_after_its_timestamp(
        int authenticationOffsetSeconds,
        SessionAdmissionState expectedState)
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        var barrier = DateTimeOffset.Parse("2026-08-09T12:00:00+00:00");
        await SetBarrierAsync(fixture, userId, barrier);
        var proof = NewProof(barrier.AddSeconds(authenticationOffsetSeconds));

        var admission = await AdmitAsync(fixture, userId, proof, AuditSource.Mcp, Guid.NewGuid());

        Assert.Equal(expectedState, admission.Result.State);
        await using var administrator = fixture.CreateAdministratorDbContext();
        if (expectedState == SessionAdmissionState.Denied)
        {
            Assert.Equal("authentication_not_after_barrier", admission.Result.Reason);
            var denial = await administrator.AuditEvents.SingleAsync();
            Assert.Equal(AuditSource.Mcp, denial.Source);
            Assert.Equal("identity_session_admission_denied", denial.Action);
            Assert.Equal(AuditOutcome.Denied, denial.Outcome);
            Assert.Null(denial.ResourceId);
            Assert.Equal("{}", denial.MetadataJson);
            Assert.Empty(await administrator.UserSessions.ToListAsync());
        }
        else
        {
            Assert.Single(await administrator.UserSessions.ToListAsync());
        }
    }

    [Fact]
    public async Task Revoked_session_is_denied_and_audited_without_a_resource_identifier()
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        var proof = NewProof();
        await SeedSessionAsync(fixture, userId, proof, revoke: true);

        var admission = await AdmitAsync(fixture, userId, proof, AuditSource.Worker, Guid.NewGuid());

        Assert.Equal(SessionAdmissionState.Denied, admission.Result.State);
        Assert.Equal("session_revoked", admission.Result.Reason);
        await using var administrator = fixture.CreateAdministratorDbContext();
        var audit = await administrator.AuditEvents.SingleAsync();
        Assert.Equal(AuditSource.Worker, audit.Source);
        Assert.Null(audit.ResourceId);
        Assert.Equal("session_revoked", audit.Reason);
    }

    [Fact]
    public async Task Session_bound_to_another_user_is_denied_without_leaking_its_resource()
    {
        await using var fixture = await CreateFixtureAsync();
        var ownerUserId = await SeedUserAsync(fixture);
        var callerUserId = await SeedUserAsync(fixture, "other-session-admission@example.test");
        var proof = NewProof();
        await SeedSessionAsync(fixture, ownerUserId, proof, revoke: false);

        var admission = await AdmitAsync(fixture, callerUserId, proof, AuditSource.Web, Guid.NewGuid());

        Assert.Equal(SessionAdmissionState.Denied, admission.Result.State);
        Assert.Equal("session_identity_mismatch", admission.Result.Reason);
        await using var administrator = fixture.CreateAdministratorDbContext();
        var audit = await administrator.AuditEvents.SingleAsync();
        Assert.Equal(callerUserId, audit.ActorUserId);
        Assert.Null(audit.ResourceId);
        Assert.Equal("session_identity_mismatch", audit.Reason);
    }

    [Fact]
    public async Task Concurrent_admissions_for_one_user_create_one_session_and_one_audit()
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        var proof = NewProof();

        var admissions = await Task.WhenAll(
            AdmitAsync(fixture, userId, proof, AuditSource.Web, Guid.NewGuid()),
            AdmitAsync(fixture, userId, proof, AuditSource.Web, Guid.NewGuid()));

        Assert.Contains(admissions, admission => admission.Result.State == SessionAdmissionState.Admitted);
        Assert.Contains(admissions, admission => admission.Result.State == SessionAdmissionState.Active);
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Single(await administrator.UserSessions.ToListAsync());
        Assert.Single(await administrator.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Audit_failure_rolls_back_a_new_session_and_returns_unavailable()
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        await RevokeAuditAppendAsync(fixture);

        var admission = await AdmitAsync(fixture, userId, NewProof(), AuditSource.Web, Guid.NewGuid());

        Assert.Equal(SessionAdmissionState.Unavailable, admission.Result.State);
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.UserSessions.ToListAsync());
        Assert.Empty(await administrator.AuditEvents.ToListAsync());
        Assert.Contains(admission.Logs, entry => entry.Level == LogLevel.Error && entry.Exception is not null);
    }

    [Fact]
    public async Task Audit_failure_for_a_policy_denial_returns_unavailable_without_a_partial_audit()
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        var barrier = DateTimeOffset.Parse("2026-08-09T12:00:00+00:00");
        await SetBarrierAsync(fixture, userId, barrier);
        await RevokeAuditAppendAsync(fixture);

        var admission = await AdmitAsync(fixture, userId, NewProof(barrier), AuditSource.Web, Guid.NewGuid());

        Assert.Equal(SessionAdmissionState.Unavailable, admission.Result.State);
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.UserSessions.ToListAsync());
        Assert.Empty(await administrator.AuditEvents.ToListAsync());
        Assert.Contains(admission.Logs, entry => entry.Level == LogLevel.Error && entry.Exception is not null);
    }

    [Fact]
    public async Task Missing_internal_user_is_unavailable_and_preserves_a_technical_exception_in_the_log()
    {
        await using var fixture = await CreateFixtureAsync();

        var admission = await AdmitAsync(fixture, Guid.NewGuid(), NewProof(), AuditSource.Web, Guid.NewGuid());

        Assert.Equal(SessionAdmissionState.Unavailable, admission.Result.State);
        Assert.Contains(admission.Logs, entry => entry.Level == LogLevel.Error && entry.Exception is not null);
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_audit_or_session()
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AdmitAsync(fixture, userId, NewProof(), AuditSource.Web, Guid.NewGuid(), cancellation.Token));

        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.UserSessions.ToListAsync());
        Assert.Empty(await administrator.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Logs_keep_correlation_in_scope_and_emit_only_safe_event_properties()
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        var proof = NewProof();
        var correlationId = Guid.NewGuid();

        var admission = await AdmitAsync(fixture, userId, proof, AuditSource.Web, correlationId);

        Assert.Equal(SessionAdmissionState.Admitted, admission.Result.State);
        var entry = Assert.Single(admission.Logs);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal("session_admission", entry.Properties["Operation"]);
        Assert.Equal("identity", entry.Properties["Module"]);
        Assert.Equal("admitted", entry.Properties["Result"]);
        Assert.Equal("new_session", entry.Properties["Reason"]);
        Assert.Equal(["Module", "Operation", "Reason", "Result"], entry.Properties.Keys.OrderBy(key => key));
        Assert.Equal(correlationId, entry.Scopes.Single()["CorrelationId"]);
        Assert.DoesNotContain(proof.ProviderSessionId.ToString(), entry.Properties.Values.Select(value => value?.ToString()));
        Assert.DoesNotContain(proof.Key.Subject, entry.Properties.Values.Select(value => value?.ToString()));
    }

    [Fact]
    public async Task Policy_denial_is_debug_and_cross_user_collision_is_warning()
    {
        await using var fixture = await CreateFixtureAsync();
        var barrierUserId = await SeedUserAsync(fixture);
        var barrier = DateTimeOffset.Parse("2026-08-09T12:00:00+00:00");
        await SetBarrierAsync(fixture, barrierUserId, barrier);
        var barrierAdmission = await AdmitAsync(
            fixture,
            barrierUserId,
            NewProof(barrier),
            AuditSource.Web,
            Guid.NewGuid());
        var ownerUserId = await SeedUserAsync(fixture, "log-owner@example.test");
        var callerUserId = await SeedUserAsync(fixture, "log-caller@example.test");
        var proof = NewProof();
        await SeedSessionAsync(fixture, ownerUserId, proof, revoke: false);
        var collisionAdmission = await AdmitAsync(
            fixture,
            callerUserId,
            proof,
            AuditSource.Web,
            Guid.NewGuid());

        Assert.Contains(barrierAdmission.Logs, entry =>
            entry.Level == LogLevel.Debug
            && entry.Properties["Reason"]?.ToString() == "authentication_not_after_barrier");
        Assert.Contains(collisionAdmission.Logs, entry =>
            entry.Level == LogLevel.Warning
            && entry.Properties["Reason"]?.ToString() == "session_identity_mismatch");
    }

    private static async Task<FoundationPostgresFixture> CreateFixtureAsync()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static async Task<Guid> SeedUserAsync(FoundationPostgresFixture fixture, string email = "session-admission@example.test")
    {
        var userId = Guid.NewGuid();
        await using var administrator = fixture.CreateAdministratorDbContext();
        administrator.Users.Add(new User(userId, "Sessão", email));
        await administrator.SaveChangesAsync();
        return userId;
    }

    private static ExternalIdentityProof NewProof(DateTimeOffset? authenticatedAt = null) => new(
        new ExternalIdentityKey("https://project.supabase.co/auth/v1", "subject-not-persisted"),
        Guid.NewGuid(),
        authenticatedAt ?? DateTimeOffset.Parse("2026-08-09T12:00:00+00:00"),
        DateTimeOffset.Parse("2026-08-09T13:00:00+00:00"));

    private static async Task SetBarrierAsync(
        FoundationPostgresFixture fixture,
        Guid userId,
        DateTimeOffset barrier)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE foundation.users SET sessions_valid_after = @barrier WHERE id = @user_id",
            administrator);
        command.Parameters.AddWithValue("barrier", barrier);
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedSessionAsync(
        FoundationPostgresFixture fixture,
        Guid userId,
        ExternalIdentityProof proof,
        bool revoke)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var session = new UserSession(
            Guid.NewGuid(),
            userId,
            proof.Key.Issuer,
            proof.ProviderSessionId,
            proof.ProviderAuthenticatedAt,
            proof.TokenExpiresAt);
        if (revoke)
        {
            session.Revoke(userId, proof.ProviderAuthenticatedAt.AddMinutes(1), "test_revocation");
        }

        administrator.UserSessions.Add(session);
        await administrator.SaveChangesAsync();
    }

    private static async Task RevokeAuditAppendAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb) FROM vinx_runtime;",
            administrator);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<AdmissionAttempt> AdmitAsync(
        FoundationPostgresFixture fixture,
        Guid userId,
        ExternalIdentityProof proof,
        AuditSource source,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        await using var runtime = fixture.CreateRuntimeDbContext();
        using var logs = new SessionAdmissionLogProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(logs));
        var service = new InternalSessionAdmissionService(
            runtime,
            new AuditAppender(runtime),
            loggerFactory.CreateLogger<InternalSessionAdmissionService>());
        var result = await service.AdmitAsync(userId, proof, source, correlationId, cancellationToken);
        return new AdmissionAttempt(result, logs.Entries.ToArray());
    }

    private sealed record AdmissionAttempt(SessionAdmissionResult Result, IReadOnlyList<CapturedLog> Logs);

    private sealed record CapturedLog(
        LogLevel Level,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Scopes);

    private sealed class SessionAdmissionLogProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

        public List<CapturedLog> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new SessionAdmissionLogger(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class SessionAdmissionLogger(SessionAdmissionLogProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopeProvider.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var properties = ((IEnumerable<KeyValuePair<string, object?>>)state!)
                    .Where(pair => pair.Key != "{OriginalFormat}")
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                var scopes = new List<IReadOnlyDictionary<string, object?>>();
                provider._scopeProvider.ForEachScope((scope, capturedScopes) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> values)
                    {
                        capturedScopes.Add(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
                    }
                }, scopes);
                provider.Entries.Add(new CapturedLog(logLevel, exception, properties, scopes));
            }
        }
    }
}
