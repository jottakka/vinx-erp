using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Observability;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class RevokeMembershipServiceIntegrationTests
{
    [Theory]
    [InlineData(CapabilityScope.Self)]
    [InlineData(CapabilityScope.Team)]
    [InlineData(CapabilityScope.Unit)]
    [InlineData(CapabilityScope.Group)]
    public async Task Restricted_scope_does_not_authorize_a_company_membership_revocation(CapabilityScope scope)
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory, administratorScope: scope);

        var result = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, Guid.NewGuid());

        Assert.Equal("capability_denied", ErrorCode(result));
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Null(await DeletedAtAsync(administrator, scenario.TargetMembershipId));
        Assert.Empty(await administrator.IdempotencyRecords.Where(record =>
            record.CommandName == "foundation.memberships.revoke").ToListAsync());
        Assert.Single(await administrator.AuditEvents.Where(audit =>
            audit.Action == "capability_denied" && audit.Outcome == AuditOutcome.Denied).ToListAsync());
    }

    [Fact]
    public async Task A_revoked_actor_membership_is_denied_and_audited()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            var actorMembership = await administrator.Memberships.SingleAsync(membership =>
                membership.Id == scenario.ActorMembershipId);
            actorMembership.Revoke("test_revocation", factory.ActorUserId, Guid.NewGuid());
            await administrator.SaveChangesAsync();
        }

        var result = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, Guid.NewGuid());

        Assert.Equal("membership_required", ErrorCode(result));
        await using var auditReader = factory.Fixture.CreateAdministratorDbContext();
        Assert.Null(await DeletedAtAsync(auditReader, scenario.TargetMembershipId));
        Assert.Single(await auditReader.AuditEvents.Where(audit =>
            audit.Action == "membership_required" && audit.Outcome == AuditOutcome.Denied).ToListAsync());
    }

    [Fact]
    public async Task Revoking_an_active_same_company_membership_soft_deletes_it_and_audits_the_transition()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);

        var result = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Equal(scenario.TargetMembershipId, result.Value!.MembershipId);
        Assert.True(result.Value.WasRevoked);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.NotNull(await DeletedAtAsync(administrator, scenario.TargetMembershipId));
        var audit = await administrator.AuditEvents.SingleAsync(auditEvent =>
            auditEvent.Action == "foundation.memberships.revoke" && auditEvent.Outcome == AuditOutcome.Success);
        Assert.Equal(scenario.TargetMembershipId, audit.ResourceId);
        Assert.Equal(scenario.CompanyId, audit.RequestedCompanyId);
        Assert.Equal(factory.ActorUserId, audit.ActorUserId);
    }

    [Fact]
    public async Task Replaying_a_revocation_after_soft_delete_returns_the_original_result_without_duplicate_audit()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);
        var requestId = Guid.NewGuid();

        var first = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, requestId);
        var replay = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, requestId);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Single(await administrator.IdempotencyRecords.Where(record =>
            record.CommandName == "foundation.memberships.revoke").ToListAsync());
        Assert.Equal(1, await administrator.AuditEvents.CountAsync(audit =>
            audit.Action == "foundation.memberships.revoke" && audit.Outcome == AuditOutcome.Success));
    }

    [Fact]
    public async Task Reusing_a_revocation_request_with_a_different_target_returns_idempotency_conflict()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);
        var requestId = Guid.NewGuid();

        var first = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, requestId);
        var conflict = await RevokeAsync(factory, scenario.CompanyId, scenario.AlternateMembershipId, requestId);

        Assert.True(first.IsSuccess);
        Assert.Equal("idempotency_conflict", ErrorCode(conflict));
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Null(await DeletedAtAsync(administrator, scenario.AlternateMembershipId));
        Assert.Single(await administrator.AuditEvents.Where(audit =>
            audit.Action == "idempotency_conflict" && audit.Outcome == AuditOutcome.Denied).ToListAsync());
    }

    [Fact]
    public async Task A_new_request_for_an_already_revoked_membership_is_hidden_as_not_found()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);

        var first = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, Guid.NewGuid());
        var second = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, Guid.NewGuid());

        Assert.True(first.IsSuccess);
        Assert.Equal("resource_not_found", ErrorCode(second));
    }

    [Fact]
    public async Task Authorization_is_evaluated_before_a_matching_replay()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);
        var requestId = Guid.NewGuid();
        var first = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, requestId);
        Assert.True(first.IsSuccess);

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            var assignment = await administrator.RoleAssignments.SingleAsync(candidate =>
                candidate.MembershipId == scenario.ActorMembershipId);
            administrator.RoleAssignments.Remove(assignment);
            await administrator.SaveChangesAsync();
        }

        var replay = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, requestId);

        Assert.Equal("capability_denied", ErrorCode(replay));
        await using var auditReader = factory.Fixture.CreateAdministratorDbContext();
        Assert.Single(await auditReader.AuditEvents.Where(audit =>
            audit.Action == "capability_denied" && audit.Outcome == AuditOutcome.Denied).ToListAsync());
    }

    [Fact]
    public async Task A_cross_company_target_membership_is_hidden_as_not_found_and_audited()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory, crossCompanyTarget: true);

        var result = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, Guid.NewGuid());

        Assert.Equal("resource_not_found", ErrorCode(result));
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Null(await DeletedAtAsync(administrator, scenario.TargetMembershipId));
        var audit = await administrator.AuditEvents.SingleAsync(auditEvent =>
            auditEvent.Action == "resource_not_found" && auditEvent.Outcome == AuditOutcome.Denied);
        Assert.Equal(scenario.CompanyId, audit.RequestedCompanyId);
        Assert.Equal(factory.CorrelationId, audit.CorrelationId);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_the_membership_revocation_and_reports_membership_telemetry()
    {
        using var listener = new MeterListener();
        var operation = default(string);
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AuthorizationTelemetry.MeterName
                && instrument.Name == AuthorizationTelemetry.AuditUnavailableCounterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "operation")
                {
                    operation = tag.Value?.ToString();
                }
            }
        });
        listener.Start();

        await using var factory = await FoundationApiFactory.CreateAsync();
        var logCapture = new RevocationLogCapture(typeof(RevokeMembershipService).FullName!);
        factory.Services.GetRequiredService<ILoggerFactory>().AddProvider(logCapture);
        var scenario = await SeedScenarioAsync(factory);
        await RevokeAuditAppendAsync(factory);

        var result = await RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, Guid.NewGuid());

        Assert.Equal("audit_unavailable", ErrorCode(result));
        Assert.Equal("membership_revocation", operation);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Null(await DeletedAtAsync(administrator, scenario.TargetMembershipId));
        Assert.Empty(await administrator.IdempotencyRecords.Where(record =>
            record.CommandName == "foundation.memberships.revoke").ToListAsync());
        Assert.Empty(await administrator.AuditEvents.ToListAsync());
        var log = Assert.Single(logCapture.Entries);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.NotNull(log.Exception);
        Assert.Equal("membership_revocation", log.Properties["Operation"]);
        Assert.Equal("foundation", log.Properties["Module"]);
        Assert.Equal("unavailable", log.Properties["Result"]);
        Assert.Equal("database_or_audit_unavailable", log.Properties["Reason"]);
        Assert.Equal(["Module", "Operation", "Reason", "Result"], log.Properties.Keys.OrderBy(key => key));
        Assert.DoesNotContain(factory.ActorUserId.ToString(), log.Properties.Values.Select(value => value?.ToString()));
        Assert.DoesNotContain(factory.CorrelationId.ToString(), log.Properties.Values.Select(value => value?.ToString()));
    }

    [Fact]
    public async Task Sixteen_concurrent_retries_create_one_transition_audit_and_idempotency_record()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);
        var requestId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            RevokeAsync(factory, scenario.CompanyId, scenario.TargetMembershipId, requestId)));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Single(results.Select(result => result.Value!.MembershipId).Distinct());
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator.IdempotencyRecords.CountAsync(record =>
            record.CommandName == "foundation.memberships.revoke"));
        Assert.Equal(1, await administrator.AuditEvents.CountAsync(audit =>
            audit.Action == "foundation.memberships.revoke" && audit.Outcome == AuditOutcome.Success));
    }

    [Fact]
    public async Task Cancellation_propagates_without_revoking_the_target()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => RevokeAsync(
            factory,
            scenario.CompanyId,
            scenario.TargetMembershipId,
            Guid.NewGuid(),
            cancellation.Token));
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Null(await DeletedAtAsync(administrator, scenario.TargetMembershipId));
    }

    private static async Task<RevocationScenario> SeedScenarioAsync(
        FoundationApiFactory factory,
        bool crossCompanyTarget = false,
        CapabilityScope administratorScope = CapabilityScope.Company)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Alfa", factory.ActorUserId, requestId);
        var otherCompany = new Company(Guid.NewGuid(), group.Id, "Beta", factory.ActorUserId, requestId);
        var actor = new User(factory.ActorUserId, "Ana", "ana@example.test");
        var target = new User(Guid.NewGuid(), "Bea", "bea@example.test");
        var alternate = new User(Guid.NewGuid(), "Caio", "caio@example.test");
        var actorMembership = new Membership(Guid.NewGuid(), company.Id, actor.Id, factory.ActorUserId, requestId);
        var targetMembership = new Membership(
            Guid.NewGuid(),
            crossCompanyTarget ? otherCompany.Id : company.Id,
            target.Id,
            factory.ActorUserId,
            requestId);
        var alternateMembership = new Membership(
            Guid.NewGuid(), company.Id, alternate.Id, factory.ActorUserId, requestId);
        var administratorRole = new Role(
            Guid.NewGuid(), company.Id, "Foundation administrator", factory.ActorUserId, requestId);
        var capability = await administrator.Capabilities.SingleAsync(candidate =>
            candidate.Name == KnownCapabilities.MembershipsRevoke);
        var grant = new RoleCapability(
            Guid.NewGuid(), company.Id, administratorRole.Id, capability.Id, administratorScope, factory.ActorUserId, requestId);
        var assignment = new RoleAssignment(
            Guid.NewGuid(), company.Id, administratorRole.Id, actorMembership.Id, factory.ActorUserId, requestId);

        administrator.AddRange(
            group,
            company,
            otherCompany,
            actor,
            target,
            alternate,
            actorMembership,
            targetMembership,
            alternateMembership,
            administratorRole,
            grant,
            assignment);
        await administrator.SaveChangesAsync();
        return new RevocationScenario(company.Id, actorMembership.Id, targetMembership.Id, alternateMembership.Id);
    }

    private static async Task<AuthorizationResult<RevokeMembershipResult>> RevokeAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid membershipId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<RevokeMembershipService>();
        return await service.RevokeAsync(
            new ActorContext(factory.ActorUserId, factory.CorrelationId),
            new RevokeMembershipCommand(companyId, membershipId, requestId),
            cancellationToken);
    }

    private static Task<DateTimeOffset?> DeletedAtAsync(
        Persistence.FoundationDbContext administrator,
        Guid membershipId) => administrator.Memberships
            .IgnoreQueryFilters()
            .Where(membership => membership.Id == membershipId)
            .Select(membership => membership.DeletedAt)
            .SingleAsync();

    private static async Task RevokeAuditAppendAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb) FROM vinx_runtime;",
            administrator);
        await command.ExecuteNonQueryAsync();
    }

    private static string? ErrorCode(AuthorizationResult<RevokeMembershipResult> result) =>
        result.ErrorCode?.ToStableCode();

    private sealed record RevocationScenario(
        Guid CompanyId,
        Guid ActorMembershipId,
        Guid TargetMembershipId,
        Guid AlternateMembershipId);

    private sealed record CapturedLog(
        LogLevel Level,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class RevocationLogCapture : ILoggerProvider, ILogger
    {
        private readonly string _categoryName;

        public RevocationLogCapture(string categoryName)
        {
            _categoryName = categoryName;
        }

        public List<CapturedLog> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) =>
            categoryName == _categoryName ? this : NullLogger.Instance;

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

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
            Entries.Add(new CapturedLog(logLevel, exception, properties));
        }
    }
}
