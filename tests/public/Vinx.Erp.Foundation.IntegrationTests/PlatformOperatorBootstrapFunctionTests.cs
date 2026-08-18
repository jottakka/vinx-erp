using System.Text.Json;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PlatformOperatorBootstrapFunctionTests
{
    private static readonly Guid DeploymentActorId = Guid.Parse(
        "00000000-0000-0000-0000-000000000001"
    );

    [Fact]
    public async Task Initial_bootstrap_creates_one_operator_identity_and_success_audit_and_replay_is_idempotent()
    {
        await using var fixture = await StartFixtureAsync();
        var request = NewRequest();

        var created = await CallAsMigratorAsync(fixture, request);
        Assert.Equal("created", created.Status);
        Assert.NotNull(created.UserId);
        Assert.NotEqual(Guid.Empty, created.UserId.Value);

        var initialState = await ReadStateAsync(fixture);
        Assert.Equal(1, initialState.Users);
        Assert.Equal(1, initialState.Identities);
        Assert.Equal(1, initialState.Operators);
        Assert.Equal(1, initialState.SuccessAudits);
        Assert.Equal("global", initialState.AuditScope);
        Assert.Equal("platform.operator.bootstrap", initialState.AuditAction);
        Assert.Equal("success", initialState.AuditOutcome);
        Assert.Equal("deployment_bootstrap", initialState.AuditReason);
        Assert.Equal(DeploymentActorId, initialState.AuditActorUserId);
        Assert.Null(initialState.RequestedCompanyId);
        Assert.Empty(JsonDocument.Parse(initialState.AuditMetadata).RootElement.EnumerateObject());
        Assert.DoesNotContain(
            request.CorrelationId.ToString(),
            initialState.AuditMetadata,
            StringComparison.Ordinal
        );

        var replay = await CallAsMigratorAsync(fixture, request);
        Assert.Equal("already_configured", replay.Status);
        Assert.Equal(created.UserId, replay.UserId);

        var replayState = await ReadStateAsync(fixture);
        Assert.Equal(initialState.Users, replayState.Users);
        Assert.Equal(initialState.Identities, replayState.Identities);
        Assert.Equal(initialState.Operators, replayState.Operators);
        Assert.Equal(initialState.SuccessAudits, replayState.SuccessAudits);
        Assert.Equal(0, replayState.DeniedAudits);
    }

    [Fact]
    public async Task Different_subject_after_bootstrap_returns_conflict_without_mutation_and_records_redacted_denial()
    {
        await using var fixture = await StartFixtureAsync();
        var initial = NewRequest();
        var changedSubject = initial with { Subject = "different-subject" };

        await CallAsMigratorAsync(fixture, initial);
        var beforeConflict = await ReadStateAsync(fixture);

        var conflict = await CallAsMigratorAsync(fixture, changedSubject);
        Assert.Equal("conflict", conflict.Status);

        var afterConflict = await ReadStateAsync(fixture);
        Assert.Equal(beforeConflict.Users, afterConflict.Users);
        Assert.Equal(beforeConflict.Identities, afterConflict.Identities);
        Assert.Equal(beforeConflict.Operators, afterConflict.Operators);
        Assert.Equal(1, afterConflict.SuccessAudits);
        Assert.Equal(1, afterConflict.DeniedAudits);
        Assert.Equal("denied", afterConflict.AuditOutcome);
        Assert.Equal("global", afterConflict.AuditScope);
        Assert.Equal("platform.operator.bootstrap", afterConflict.AuditAction);
        Assert.Equal(DeploymentActorId, afterConflict.AuditActorUserId);
        Assert.Empty(JsonDocument.Parse(afterConflict.AuditMetadata).RootElement.EnumerateObject());
        Assert.DoesNotContain(
            initial.CorrelationId.ToString(),
            afterConflict.AuditMetadata,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            changedSubject.CorrelationId.ToString(),
            afterConflict.AuditMetadata,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            initial.Subject,
            afterConflict.AuditMetadata,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            changedSubject.Subject,
            afterConflict.AuditMetadata,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            initial.ProviderEmail,
            afterConflict.AuditMetadata,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task Different_identity_for_the_same_internal_user_still_returns_conflict()
    {
        await using var fixture = await StartFixtureAsync();
        var initial = NewRequest();
        var created = await CallAsMigratorAsync(fixture, initial);
        var alternate = initial with
        {
            Subject = "alternate-subject-for-same-user",
            CorrelationId = Guid.NewGuid(),
        };

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (
            var seedAlternateIdentity = new NpgsqlCommand(
                """
                INSERT INTO foundation.external_identities (id, user_id, issuer, subject, created_at)
                VALUES (@id, @user_id, @issuer, @subject, clock_timestamp())
                """,
                administrator
            )
        )
        {
            seedAlternateIdentity.Parameters.AddWithValue("id", Guid.NewGuid());
            seedAlternateIdentity.Parameters.AddWithValue("user_id", created.UserId!.Value);
            seedAlternateIdentity.Parameters.AddWithValue("issuer", alternate.Issuer);
            seedAlternateIdentity.Parameters.AddWithValue("subject", alternate.Subject);
            await seedAlternateIdentity.ExecuteNonQueryAsync();
        }

        var result = await CallAsMigratorAsync(fixture, alternate);

        Assert.Equal("conflict", result.Status);
        var state = await ReadStateAsync(fixture);
        Assert.Equal(1, state.Users);
        Assert.Equal(2, state.Identities);
        Assert.Equal(1, state.Operators);
        Assert.Equal(1, state.SuccessAudits);
        Assert.Equal(1, state.DeniedAudits);
    }

    [Fact]
    public async Task Existing_email_collision_returns_conflict_without_attaching_identity_or_operator()
    {
        await using var fixture = await StartFixtureAsync();
        var request = NewRequest();
        var existingUserId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (
            var seed = new NpgsqlCommand(
                "INSERT INTO foundation.users (id, display_name, email, created_at) VALUES (@id, 'Existing', @email, clock_timestamp())",
                administrator
            )
        )
        {
            seed.Parameters.AddWithValue("id", existingUserId);
            seed.Parameters.AddWithValue("email", request.ProviderEmail);
            await seed.ExecuteNonQueryAsync();
        }

        var result = await CallAsMigratorAsync(fixture, request);
        Assert.Equal("conflict", result.Status);

        var state = await ReadStateAsync(fixture);
        Assert.Equal(1, state.Users);
        Assert.Equal(0, state.Identities);
        Assert.Equal(0, state.Operators);
        Assert.Equal(0, state.SuccessAudits);
        Assert.Equal(1, state.DeniedAudits);
        Assert.Equal("denied", state.AuditOutcome);
        Assert.Equal(DeploymentActorId, state.AuditActorUserId);
        Assert.Empty(JsonDocument.Parse(state.AuditMetadata).RootElement.EnumerateObject());
        Assert.DoesNotContain(
            request.CorrelationId.ToString(),
            state.AuditMetadata,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(request.ProviderEmail, state.AuditMetadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_bootstrap_requests_have_one_creation_and_no_duplicate_success_audit()
    {
        await using var fixture = await StartFixtureAsync();
        var request = NewRequest();

        var results = await RunConcurrentAsync(
            fixture,
            Enumerable.Range(0, 8).Select(_ => request).ToArray()
        );

        Assert.Equal(1, results.Count(result => result.Status == "created"));
        Assert.Equal(7, results.Count(result => result.Status == "already_configured"));
        var userIds = results.Select(result => result.UserId).Distinct().ToArray();
        Assert.Single(userIds);
        Assert.NotNull(userIds[0]);

        var state = await ReadStateAsync(fixture);
        Assert.Equal(1, state.Users);
        Assert.Equal(1, state.Identities);
        Assert.Equal(1, state.Operators);
        Assert.Equal(1, state.SuccessAudits);
        Assert.Equal(0, state.DeniedAudits);
    }

    [Fact]
    public async Task Concurrent_different_subjects_have_one_winner_and_no_partial_loser_state()
    {
        await using var fixture = await StartFixtureAsync();
        var requests = Enumerable
            .Range(1, 8)
            .Select(index => NewRequest() with { Subject = $"subject-operator-{index}" })
            .ToArray();

        var results = await RunConcurrentAsync(fixture, requests);

        Assert.Equal(1, results.Count(result => result.Status == "created"));
        Assert.Equal(7, results.Count(result => result.Status == "conflict"));

        var state = await ReadStateAsync(fixture);
        Assert.Equal(1, state.Users);
        Assert.Equal(1, state.Identities);
        Assert.Equal(1, state.Operators);
        Assert.Equal(1, state.SuccessAudits);
        Assert.Equal(7, state.DeniedAudits);
        Assert.Equal("denied", state.AuditOutcome);
        Assert.Equal(DeploymentActorId, state.AuditActorUserId);
        Assert.Empty(JsonDocument.Parse(state.AuditMetadata).RootElement.EnumerateObject());
        foreach (var request in requests)
        {
            Assert.DoesNotContain(request.Subject, state.AuditMetadata, StringComparison.Ordinal);
            Assert.DoesNotContain(
                request.CorrelationId.ToString(),
                state.AuditMetadata,
                StringComparison.Ordinal
            );
        }
    }

    [Fact]
    public async Task Audit_append_failure_rolls_back_all_bootstrap_writes()
    {
        await using var fixture = await StartFixtureAsync();
        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (
            var revoke = new NpgsqlCommand(
                "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb) FROM vinx_platform_owner;",
                administrator
            )
        )
        {
            await revoke.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            CallAsMigratorAsync(fixture, NewRequest())
        );
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);

        var state = await ReadStateAsync(fixture);
        Assert.Equal(0, state.Users);
        Assert.Equal(0, state.Identities);
        Assert.Equal(0, state.Operators);
        Assert.Equal(0, state.SuccessAudits);
        Assert.Equal(0, state.DeniedAudits);
    }

    [Fact]
    public async Task Runtime_cannot_execute_the_platform_operator_bootstrap_function()
    {
        await using var fixture = await StartFixtureAsync();
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT * FROM foundation.bootstrap_platform_operator(@subject, @issuer, @display_name, @provider_email, @correlation_id)",
            runtime
        );
        command.Parameters.AddWithValue("subject", "runtime-subject");
        command.Parameters.AddWithValue("issuer", "https://issuer.bootstrap.test");
        command.Parameters.AddWithValue("display_name", "Runtime Attempt");
        command.Parameters.AddWithValue("provider_email", "runtime@bootstrap.test");
        command.Parameters.AddWithValue("correlation_id", Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            command.ExecuteScalarAsync()
        );
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private static async Task<FoundationPostgresFixture> StartFixtureAsync()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static BootstrapRequest NewRequest() =>
        new(
            "https://issuer.bootstrap.test",
            "subject-operator-1",
            "Deployment Operator",
            "operator@bootstrap.test",
            Guid.NewGuid()
        );

    private static async Task<BootstrapResult> CallAsMigratorAsync(
        FoundationPostgresFixture fixture,
        BootstrapRequest request
    )
    {
        await using var migrator = new NpgsqlConnection(fixture.MigratorConnectionString);
        await migrator.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status, user_id FROM foundation.bootstrap_platform_operator(@subject, @issuer, @display_name, @provider_email, @correlation_id)",
            migrator
        );
        command.Parameters.AddWithValue("subject", request.Subject);
        command.Parameters.AddWithValue("issuer", request.Issuer);
        command.Parameters.AddWithValue("display_name", request.DisplayName);
        command.Parameters.AddWithValue("provider_email", request.ProviderEmail);
        command.Parameters.AddWithValue("correlation_id", request.CorrelationId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = new BootstrapResult(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1)
        );
        Assert.False(await reader.ReadAsync());
        return result;
    }

    private static async Task<BootstrapResult[]> RunConcurrentAsync(
        FoundationPostgresFixture fixture,
        BootstrapRequest[] requests
    )
    {
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var ready = requests
            .Select(_ => new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            ))
            .ToArray();
        var calls = requests
            .Select(
                (request, index) =>
                    Task.Run(async () =>
                    {
                        ready[index].SetResult(true);
                        await release.Task;
                        return await CallAsMigratorAsync(fixture, request);
                    })
            )
            .ToArray();

        await Task.WhenAll(ready.Select(signal => signal.Task));
        release.SetResult(true);
        return await Task.WhenAll(calls);
    }

    private static async Task<BootstrapState> ReadStateAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM foundation.users),
                (SELECT count(*) FROM foundation.external_identities),
                (SELECT count(*) FROM foundation.platform_operators),
                (SELECT count(*) FROM audit.audit_events WHERE action = 'platform.operator.bootstrap' AND outcome = 'success'),
                (SELECT count(*) FROM audit.audit_events WHERE action = 'platform.operator.bootstrap' AND outcome = 'denied'),
                (SELECT scope FROM audit.audit_events WHERE action = 'platform.operator.bootstrap' ORDER BY occurred_at DESC LIMIT 1),
                (SELECT action FROM audit.audit_events WHERE action = 'platform.operator.bootstrap' ORDER BY occurred_at DESC LIMIT 1),
                (SELECT outcome FROM audit.audit_events WHERE action = 'platform.operator.bootstrap' ORDER BY occurred_at DESC LIMIT 1),
                (SELECT reason FROM audit.audit_events WHERE action = 'platform.operator.bootstrap' ORDER BY occurred_at DESC LIMIT 1),
                (SELECT metadata_json::text FROM audit.audit_events WHERE action = 'platform.operator.bootstrap' ORDER BY occurred_at DESC LIMIT 1),
                (SELECT actor_user_id FROM audit.audit_events WHERE action = 'platform.operator.bootstrap' ORDER BY occurred_at DESC LIMIT 1),
                (SELECT requested_company_id FROM audit.audit_events WHERE action = 'platform.operator.bootstrap' ORDER BY occurred_at DESC LIMIT 1)
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new BootstrapState(
            reader.GetFieldValue<long>(0),
            reader.GetFieldValue<long>(1),
            reader.GetFieldValue<long>(2),
            reader.GetFieldValue<long>(3),
            reader.GetFieldValue<long>(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? "" : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetGuid(10),
            reader.IsDBNull(11) ? null : reader.GetGuid(11)
        );
    }

    private sealed record BootstrapRequest(
        string Issuer,
        string Subject,
        string DisplayName,
        string ProviderEmail,
        Guid CorrelationId
    );

    private sealed record BootstrapResult(string Status, Guid? UserId);

    private sealed record BootstrapState(
        long Users,
        long Identities,
        long Operators,
        long SuccessAudits,
        long DeniedAudits,
        string? AuditScope,
        string? AuditAction,
        string? AuditOutcome,
        string? AuditReason,
        string AuditMetadata,
        Guid? AuditActorUserId,
        Guid? RequestedCompanyId
    );
}
