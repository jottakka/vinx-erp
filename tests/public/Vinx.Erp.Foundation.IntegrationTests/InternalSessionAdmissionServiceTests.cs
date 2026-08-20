using System.Reflection;
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

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Known_session_not_newer_than_barrier_is_denied_and_audited(
        int authenticationOffsetSeconds)
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        var barrier = DateTimeOffset.Parse("2026-08-09T12:00:00+00:00");
        var proof = NewProof(barrier.AddSeconds(authenticationOffsetSeconds));
        await SeedSessionAsync(fixture, userId, proof, revoke: false);
        await SetBarrierAsync(fixture, userId, barrier);

        var admission = await AdmitAsync(fixture, userId, proof, AuditSource.Mcp, Guid.NewGuid());

        Assert.Equal(SessionAdmissionState.Denied, admission.Result.State);
        Assert.Equal("authentication_not_after_barrier", admission.Result.Reason);
        Assert.DoesNotContain(admission.Logs, entry =>
            entry.Properties.TryGetValue("Result", out var result)
            && result?.ToString() == "active");
        await using var administrator = fixture.CreateAdministratorDbContext();
        var audits = await administrator.AuditEvents.ToListAsync();
        var denial = Assert.Single(audits);
        Assert.Equal("identity_session_admission_denied", denial.Action);
        Assert.Equal(AuditOutcome.Denied, denial.Outcome);
        Assert.Equal("authentication_not_after_barrier", denial.Reason);
        Assert.Equal(AuditSource.Mcp, denial.Source);
        Assert.Null(denial.ResourceId);
        Assert.Single(await administrator.UserSessions.ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Unique_race_replay_revalidates_an_equal_or_newer_barrier_before_returning_active(
        int barrierOffsetSeconds)
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        var proof = NewProof();
        await SeedSessionAsync(fixture, userId, proof, revoke: false);
        await SetBarrierAsync(fixture, userId, proof.ProviderAuthenticatedAt.AddSeconds(barrierOffsetSeconds));
        var correlationId = Guid.NewGuid();

        var replay = await ResolveUniqueRaceAsync(fixture, userId, proof, AuditSource.Mcp, correlationId);

        Assert.Equal(SessionAdmissionState.Denied, replay.Result.State);
        Assert.Equal("authentication_not_after_barrier", replay.Result.Reason);
        Assert.DoesNotContain(replay.Logs, entry =>
            entry.Properties.TryGetValue("Result", out var result)
            && result?.ToString() == "active");
        var log = Assert.Single(replay.Logs);
        Assert.Equal(LogLevel.Debug, log.Level);
        Assert.Equal("session_admission", log.Properties["Operation"]);
        Assert.Equal("identity", log.Properties["Module"]);
        Assert.Equal("denied", log.Properties["Result"]);
        Assert.Equal("authentication_not_after_barrier", log.Properties["Reason"]);
        Assert.Equal(["Module", "Operation", "Reason", "Result"], log.Properties.Keys.OrderBy(key => key));

        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Single(await administrator.UserSessions.ToListAsync());
        var denial = Assert.Single(await administrator.AuditEvents.ToListAsync());
        Assert.Equal("identity_session_admission_denied", denial.Action);
        Assert.Equal(AuditOutcome.Denied, denial.Outcome);
        Assert.Equal("authentication_not_after_barrier", denial.Reason);
        Assert.Null(denial.ResourceId);
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
    public async Task Concurrent_cross_user_admissions_resolve_the_unique_insert_race_without_leaking_the_owner()
    {
        await using var fixture = await CreateFixtureAsync();
        var firstUserId = await SeedUserAsync(fixture, "first-race-user@example.test");
        var secondUserId = await SeedUserAsync(fixture, "second-race-user@example.test");
        var proof = NewProof();
        var firstCorrelationId = Guid.NewGuid();
        var secondCorrelationId = Guid.NewGuid();
        await InstallSessionInsertRaceBarrierAsync(fixture);
        await using var coordinator = await fixture.OpenAdministratorConnectionAsync();
        await AcquireRaceCoordinatorLockAsync(coordinator);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var firstAdmission = AdmitAsync(
                fixture,
                firstUserId,
                proof,
                AuditSource.Web,
                firstCorrelationId,
                cancellation.Token);
            var secondAdmission = AdmitAsync(
                fixture,
                secondUserId,
                proof,
                AuditSource.Mcp,
                secondCorrelationId,
                cancellation.Token);

            await WaitForRaceArrivalsAsync(coordinator, cancellation.Token);
            await ReleaseRaceCoordinatorLockAsync(coordinator);
            var attempts = await Task.WhenAll(firstAdmission, secondAdmission);
            var firstAttempt = (UserId: firstUserId, Source: AuditSource.Web, CorrelationId: firstCorrelationId, Admission: attempts[0]);
            var secondAttempt = (UserId: secondUserId, Source: AuditSource.Mcp, CorrelationId: secondCorrelationId, Admission: attempts[1]);
            var allAttempts = new[] { firstAttempt, secondAttempt };
            var (UserId, Source, CorrelationId, Admission) = Assert.Single(allAttempts, attempt => attempt.Admission.Result.State == SessionAdmissionState.Admitted);
            var denied = Assert.Single(allAttempts, attempt => attempt.Admission.Result.State == SessionAdmissionState.Denied);

            Assert.Equal("session_identity_mismatch", denied.Admission.Result.Reason);
            await using var administrator = fixture.CreateAdministratorDbContext();
            var session = await administrator.UserSessions.SingleAsync();
            var audits = await administrator.AuditEvents.ToListAsync();
            var success = Assert.Single(audits, audit => audit.Action == "identity_session_admitted");
            var denial = Assert.Single(audits, audit => audit.Action == "identity_session_admission_denied");

            Assert.Equal(UserId, session.UserId);
            Assert.Equal(UserId, success.ActorUserId);
            Assert.Equal(session.Id, success.ResourceId);
            Assert.Equal(denied.UserId, denial.ActorUserId);
            Assert.Equal(denied.Source, denial.Source);
            Assert.Equal(denied.CorrelationId, denial.CorrelationId);
            Assert.Equal(AuditOutcome.Denied, denial.Outcome);
            Assert.Equal("session_identity_mismatch", denial.Reason);
            Assert.Null(denial.ResourceId);
            Assert.Equal("{}", denial.MetadataJson);
            Assert.NotEqual(session.Id, denial.ResourceId);
        }
        finally
        {
            await ReleaseRaceCoordinatorLockAsync(coordinator);
            await RemoveSessionInsertRaceBarrierAsync(fixture);
        }
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
    public async Task Database_failure_logs_a_sanitized_error_without_identity_values()
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        var proof = NewProof();
        var correlationId = Guid.NewGuid();
        await InstallSensitiveSessionInsertFailureAsync(fixture);

        try
        {
            var admission = await AdmitAsync(fixture, userId, proof, AuditSource.Web, correlationId);

            Assert.Equal(SessionAdmissionState.Unavailable, admission.Result.State);
            var entry = Assert.Single(admission.Logs);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.NotNull(entry.Exception);
            Assert.Null(entry.Exception!.InnerException);
            Assert.Equal("session_admission", entry.Properties["Operation"]);
            Assert.Equal("identity", entry.Properties["Module"]);
            Assert.Equal("unavailable", entry.Properties["Result"]);
            Assert.Equal("database_or_audit_unavailable", entry.Properties["Reason"]);
            Assert.Equal(["Module", "Operation", "Reason", "Result"], entry.Properties.Keys.OrderBy(key => key));
            Assert.Equal(correlationId, entry.Scopes.Single()["CorrelationId"]);
            Assert.DoesNotContain("CorrelationId", entry.Properties.Keys);

            var rendered = string.Join("\n", entry.RenderedMessage, entry.Exception.Message, entry.Exception.ToString());
            Assert.DoesNotContain(proof.ProviderSessionId.ToString(), rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(proof.Key.Issuer, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(proof.Key.Subject, rendered, StringComparison.Ordinal);
        }
        finally
        {
            await RemoveSensitiveSessionInsertFailureAsync(fixture);
        }
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

    private static async Task InstallSessionInsertRaceBarrierAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            CREATE OR REPLACE FUNCTION foundation.coordinate_session_insert_race_for_test()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                PERFORM pg_advisory_lock_shared(190753, 2);
                PERFORM pg_advisory_lock(190753, 1);
                PERFORM pg_advisory_unlock(190753, 1);
                PERFORM pg_advisory_unlock_shared(190753, 2);
                RETURN NEW;
            END;
            $$;

            CREATE TRIGGER tr_coordinate_session_insert_race_for_test
            BEFORE INSERT ON foundation.user_sessions
            FOR EACH ROW
            EXECUTE FUNCTION foundation.coordinate_session_insert_race_for_test();
            """, administrator);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RemoveSessionInsertRaceBarrierAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            DROP TRIGGER IF EXISTS tr_coordinate_session_insert_race_for_test ON foundation.user_sessions;
            DROP FUNCTION IF EXISTS foundation.coordinate_session_insert_race_for_test();
            """, administrator);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AcquireRaceCoordinatorLockAsync(NpgsqlConnection coordinator)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(190753, 1);", coordinator);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ReleaseRaceCoordinatorLockAsync(NpgsqlConnection coordinator)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(190753, 1);", coordinator);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForRaceArrivalsAsync(NpgsqlConnection coordinator, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM pg_locks
                WHERE locktype = 'advisory'
                  AND classid = 190753
                  AND objid = 2
                  AND objsubid = 2
                  AND mode = 'ShareLock'
                  AND granted
                """, coordinator);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            if (count == 2)
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new Xunit.Sdk.XunitException("The two session inserts did not reach the race barrier.");
    }

    private static async Task InstallSensitiveSessionInsertFailureAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            CREATE OR REPLACE FUNCTION foundation.fail_session_insert_with_identity_for_test()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'provider session % issuer % subject subject-not-persisted', NEW.provider_session_id, NEW.issuer;
            END;
            $$;

            CREATE TRIGGER tr_fail_session_insert_with_identity_for_test
            BEFORE INSERT ON foundation.user_sessions
            FOR EACH ROW
            EXECUTE FUNCTION foundation.fail_session_insert_with_identity_for_test();
            """, administrator);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RemoveSensitiveSessionInsertFailureAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            DROP TRIGGER IF EXISTS tr_fail_session_insert_with_identity_for_test ON foundation.user_sessions;
            DROP FUNCTION IF EXISTS foundation.fail_session_insert_with_identity_for_test();
            """, administrator);
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

    private static async Task<AdmissionAttempt> ResolveUniqueRaceAsync(
        FoundationPostgresFixture fixture,
        Guid userId,
        ExternalIdentityProof proof,
        AuditSource source,
        Guid correlationId)
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
        var method = typeof(InternalSessionAdmissionService).GetMethod(
            "ResolveUniqueRaceAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<SessionAdmissionResult>)method.Invoke(
            service,
            [userId, proof, source, correlationId, new Exception("Test unique replay"), CancellationToken.None])!;
        var result = await task;
        return new AdmissionAttempt(result, logs.Entries.ToArray());
    }

    private sealed record AdmissionAttempt(SessionAdmissionResult Result, IReadOnlyList<CapturedLog> Logs);

    private sealed record CapturedLog(
        LogLevel Level,
        Exception? Exception,
        string RenderedMessage,
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
                provider.Entries.Add(new CapturedLog(logLevel, exception, formatter(state, exception), properties, scopes));
            }
        }
    }
}
