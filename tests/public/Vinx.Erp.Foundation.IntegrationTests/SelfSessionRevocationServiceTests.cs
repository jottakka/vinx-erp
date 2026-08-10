using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class SelfSessionRevocationServiceTests
{
    [Fact]
    public async Task Revoke_all_revokes_every_active_own_session_advances_barrier_and_appends_one_audit()
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        await SeedActiveSessionAsync(fixture, userId);
        await SeedActiveSessionAsync(fixture, userId);
        var correlationId = Guid.NewGuid();

        var result = await RevokeAsync(fixture, userId, correlationId);

        Assert.Equal(SelfSessionRevocationState.Revoked, result.State);
        await using var administrator = fixture.CreateAdministratorDbContext();
        var user = await administrator.Users.SingleAsync();
        var sessions = await administrator.UserSessions.OrderBy(session => session.Id).ToListAsync();
        var audit = await administrator.AuditEvents.SingleAsync();
        Assert.NotNull(user.SessionsValidAfter);
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, session =>
        {
            Assert.Equal(user.SessionsValidAfter, session.RevokedAt);
            Assert.Equal(userId, session.RevokedByUserId);
            Assert.Equal("self_revoke_all", session.RevocationReason);
        });
        Assert.Equal("identity_sessions_revoked", audit.Action);
        Assert.Equal(AuditScope.Global, audit.Scope);
        Assert.Equal(AuditOutcome.Success, audit.Outcome);
        Assert.Equal(userId, audit.ActorUserId);
        Assert.Equal(userId, audit.ResourceId);
        Assert.Equal(correlationId, audit.CorrelationId);
        using var metadata = JsonDocument.Parse(audit.MetadataJson);
        var property = Assert.Single(metadata.RootElement.EnumerateObject());
        Assert.Equal("revoked_session_count", property.Name);
        Assert.Equal(JsonValueKind.Number, property.Value.ValueKind);
        Assert.Equal(2, property.Value.GetInt32());
    }

    [Fact]
    public async Task No_change_preserves_barrier_and_denies_an_unobserved_old_proof()
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        await SeedActiveSessionAsync(fixture, userId);

        var first = await RevokeAsync(fixture, userId, Guid.NewGuid());
        var barrier = await ReadBarrierAsync(fixture, userId);
        var second = await RevokeAsync(fixture, userId, Guid.NewGuid());

        Assert.Equal(SelfSessionRevocationState.Revoked, first.State);
        Assert.Equal(SelfSessionRevocationState.NoChange, second.State);
        Assert.Equal(barrier, await ReadBarrierAsync(fixture, userId));

        await using var runtime = fixture.CreateRuntimeDbContext();
        var admission = new InternalSessionAdmissionService(
            runtime,
            new AuditAppender(runtime),
            NullLogger<InternalSessionAdmissionService>.Instance);
        var oldProof = new ExternalIdentityProof(
            new ExternalIdentityKey("https://project.supabase.co/auth/v1", "unobserved-subject"),
            Guid.NewGuid(),
            barrier!.Value.AddSeconds(-1),
            barrier.Value.AddHours(1));

        var result = await admission.AdmitAsync(
            userId,
            oldProof,
            AuditSource.Web,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(SessionAdmissionState.Denied, result.State);
        Assert.Equal("authentication_not_after_barrier", result.Reason);
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Equal(2, await administrator.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Audit_failure_rolls_back_revocations_and_barrier()
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        await SeedActiveSessionAsync(fixture, userId);
        await RevokeAuditAppendAsync(fixture);

        var result = await RevokeAsync(fixture, userId, Guid.NewGuid());

        Assert.Equal(SelfSessionRevocationState.Unavailable, result.State);
        await using var administrator = fixture.CreateAdministratorDbContext();
        var user = await administrator.Users.SingleAsync();
        var session = await administrator.UserSessions.SingleAsync();
        Assert.Null(user.SessionsValidAfter);
        Assert.Null(session.RevokedAt);
        Assert.Null(session.RevokedByUserId);
        Assert.Null(session.RevocationReason);
        Assert.Empty(await administrator.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_revocations_create_one_success_audit_and_one_no_change()
    {
        await using var fixture = await CreateFixtureAsync();
        var userId = await SeedUserAsync(fixture);
        await SeedActiveSessionAsync(fixture, userId);

        var results = await Task.WhenAll(
            RevokeAsync(fixture, userId, Guid.NewGuid()),
            RevokeAsync(fixture, userId, Guid.NewGuid()));

        Assert.Contains(results, result => result.State == SelfSessionRevocationState.Revoked);
        Assert.Contains(results, result => result.State == SelfSessionRevocationState.NoChange);
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Single(await administrator.AuditEvents.ToListAsync());
        var session = await administrator.UserSessions.SingleAsync();
        Assert.NotNull(session.RevokedAt);
    }

    private static async Task<FoundationPostgresFixture> CreateFixtureAsync()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static async Task<Guid> SeedUserAsync(FoundationPostgresFixture fixture)
    {
        var userId = Guid.NewGuid();
        await using var administrator = fixture.CreateAdministratorDbContext();
        administrator.Users.Add(new User(userId, "Revogação", $"revoke-{userId:N}@example.test"));
        await administrator.SaveChangesAsync();
        return userId;
    }

    private static async Task SeedActiveSessionAsync(FoundationPostgresFixture fixture, Guid userId)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        administrator.UserSessions.Add(new UserSession(
            Guid.NewGuid(),
            userId,
            "https://project.supabase.co/auth/v1",
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-08-09T12:00:00+00:00"),
            DateTimeOffset.Parse("2026-08-09T13:00:00+00:00")));
        await administrator.SaveChangesAsync();
    }

    private static async Task<SelfSessionRevocationResult> RevokeAsync(
        FoundationPostgresFixture fixture,
        Guid userId,
        Guid correlationId)
    {
        await using var runtime = fixture.CreateRuntimeDbContext();
        var service = new SelfSessionRevocationService(runtime, new AuditAppender(runtime));
        return await service.RevokeAllAsync(
            new ActorContext(userId, correlationId),
            AuditSource.Web,
            CancellationToken.None);
    }

    private static async Task<DateTimeOffset?> ReadBarrierAsync(FoundationPostgresFixture fixture, Guid userId)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        return await administrator.Users
            .Where(user => user.Id == userId)
            .Select(user => user.SessionsValidAfter)
            .SingleAsync();
    }

    private static async Task RevokeAuditAppendAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb) FROM vinx_runtime;",
            administrator);
        await command.ExecuteNonQueryAsync();
    }
}
