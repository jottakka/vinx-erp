using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.Foundation.Approvals;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class ApprovalServicesIntegrationTests
{
    private const string PolicyVersion = "2026-08-14.1";
    private const string Action = "ai.writes.record";

    [Fact]
    public async Task Preparation_creates_a_live_approval_with_idempotency_record_and_audit()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var requestId = Guid.NewGuid();

        var result = await PrepareAsync(fixture, scenario, requestId);

        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.Value!.ApprovalId);
        Assert.NotEqual(Guid.Empty, result.Value.ProposalId);
        Assert.True(result.Value.ExpiresAt > DateTimeOffset.UtcNow);
        await using var administrator = fixture.CreateAdministratorDbContext();
        var approval = await administrator.Approvals.SingleAsync();
        Assert.Equal(result.Value.ApprovalId, approval.Id);
        Assert.Equal(result.Value.ProposalId, approval.ProposalId);
        Assert.Equal(scenario.CompanyId, approval.CompanyId);
        Assert.Equal(scenario.ActorUserId, approval.ActorUserId);
        Assert.Null(approval.ConsumedAt);
        Assert.Single(await administrator.IdempotencyRecords.Where(record =>
            record.CommandName == "ai.approvals.prepare").ToListAsync());
        var audit = await administrator.AuditEvents.SingleAsync();
        Assert.Equal("ai.approvals.prepare", audit.Action);
        Assert.Equal(AuditSource.Ai, audit.Source);
        Assert.Equal(AuditOutcome.Success, audit.Outcome);
        Assert.Equal(approval.Id, audit.ResourceId);
    }

    [Fact]
    public async Task Replaying_the_preparation_request_returns_the_same_approval_without_duplicate_audit()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var requestId = Guid.NewGuid();

        var first = await PrepareAsync(fixture, scenario, requestId);
        var replay = await PrepareAsync(fixture, scenario, requestId);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Single(await administrator.Approvals.ToListAsync());
        Assert.Equal(1, await administrator.AuditEvents.CountAsync(audit =>
            audit.Action == "ai.approvals.prepare" && audit.Outcome == AuditOutcome.Success));
    }

    [Fact]
    public async Task Reusing_the_preparation_request_with_a_different_payload_is_an_idempotency_conflict()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var requestId = Guid.NewGuid();

        var first = await PrepareAsync(fixture, scenario, requestId);
        var conflict = await PrepareAsync(
            fixture, scenario, requestId, Payload("""{"alvo":"outro"}"""));

        Assert.True(first.IsSuccess);
        Assert.Equal("idempotency_conflict", ErrorCode(conflict));
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Single(await administrator.Approvals.ToListAsync());
        Assert.Single(await administrator.AuditEvents.Where(audit =>
            audit.Action == "idempotency_conflict" && audit.Outcome == AuditOutcome.Denied).ToListAsync());
    }

    [Fact]
    public async Task A_second_preparation_while_a_proposal_is_live_is_denied_fail_closed_and_audited()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);

        var first = await PrepareAsync(fixture, scenario, Guid.NewGuid());
        var second = await PrepareAsync(fixture, scenario, Guid.NewGuid());

        Assert.True(first.IsSuccess);
        Assert.Equal("proposal_conflict", ErrorCode(second));
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Single(await administrator.Approvals.ToListAsync());
        Assert.Single(await administrator.AuditEvents.Where(audit =>
            audit.Action == "proposal_conflict" && audit.Outcome == AuditOutcome.Denied).ToListAsync());
    }

    [Fact]
    public async Task Preparation_without_membership_is_denied_and_audited()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var outsider = Guid.NewGuid();

        var result = await PrepareAsync(fixture, scenario, Guid.NewGuid(), actorUserId: outsider);

        Assert.Equal("membership_required", ErrorCode(result));
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.Approvals.ToListAsync());
        Assert.Single(await administrator.AuditEvents.Where(audit =>
            audit.Action == "membership_required" && audit.Outcome == AuditOutcome.Denied).ToListAsync());
    }

    [Fact]
    public async Task Preparation_without_the_capability_is_denied_and_audited()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture, grantCapability: false);

        var result = await PrepareAsync(fixture, scenario, Guid.NewGuid());

        Assert.Equal("capability_denied", ErrorCode(result));
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.Approvals.ToListAsync());
        Assert.Single(await administrator.AuditEvents.Where(audit =>
            audit.Action == "capability_denied" && audit.Outcome == AuditOutcome.Denied).ToListAsync());
    }

    [Fact]
    public async Task A_restricted_capability_scope_does_not_authorize_preparation()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture, capabilityScope: CapabilityScope.Self);

        var result = await PrepareAsync(fixture, scenario, Guid.NewGuid());

        Assert.Equal("capability_denied", ErrorCode(result));
    }

    [Fact]
    public async Task Confirming_a_live_proposal_claims_it_once_with_the_database_clock_and_audits()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var payload = Payload("""{"alvo":"estoque"}""");
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), payload);
        Assert.True(prepared.IsSuccess);

        var result = await ConfirmAsync(fixture, scenario, prepared.Value!, Guid.NewGuid(), payload);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConfirmApprovalOutcome.Claimed, result.Value!.Outcome);
        Assert.Equal(prepared.Value!.ProposalId, result.Value.ProposalId);
        await using var administrator = fixture.CreateAdministratorDbContext();
        var approval = await administrator.Approvals.SingleAsync();
        Assert.NotNull(approval.ConsumedAt);
        Assert.NotNull(approval.ConfirmationRequestId);
        Assert.NotNull(approval.ResultJson);
        Assert.Equal(1, await administrator.AuditEvents.CountAsync(audit =>
            audit.Action == "ai.approvals.confirm" && audit.Outcome == AuditOutcome.Success));
    }

    [Fact]
    public async Task Replaying_the_winning_confirmation_returns_the_original_outcome_without_new_audit()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var payload = Payload("""{"alvo":"estoque"}""");
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), payload);
        Assert.True(prepared.IsSuccess);
        var confirmationRequestId = Guid.NewGuid();
        var first = await ConfirmAsync(fixture, scenario, prepared.Value!, confirmationRequestId, payload);

        var replay = await ConfirmAsync(fixture, scenario, prepared.Value!, confirmationRequestId, payload);

        Assert.True(first.IsSuccess);
        Assert.Equal(ConfirmApprovalOutcome.Claimed, first.Value!.Outcome);
        Assert.True(replay.IsSuccess);
        Assert.Equal(ConfirmApprovalOutcome.Replay, replay.Value!.Outcome);
        Assert.Equal(first.Value.ProposalId, replay.Value.ProposalId);
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator.AuditEvents.CountAsync(audit =>
            audit.Action == "ai.approvals.confirm" && audit.Outcome == AuditOutcome.Success));
    }

    [Fact]
    public async Task A_confirmation_with_a_different_request_after_consumption_is_a_conflict_and_audited()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var payload = Payload("""{"alvo":"estoque"}""");
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), payload);
        Assert.True(prepared.IsSuccess);
        var winner = await ConfirmAsync(fixture, scenario, prepared.Value!, Guid.NewGuid(), payload);
        Assert.True(winner.IsSuccess);

        var late = await ConfirmAsync(fixture, scenario, prepared.Value!, Guid.NewGuid(), payload);

        Assert.Equal("proposal_conflict", ErrorCode(late));
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Single(await administrator.AuditEvents.Where(audit =>
            audit.Action == "proposal_conflict" && audit.Outcome == AuditOutcome.Denied).ToListAsync());
    }

    [Fact]
    public async Task Confirmations_with_payload_or_policy_divergence_are_conflicts_and_audited()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var payload = Payload("""{"alvo":"estoque"}""");
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), payload);
        Assert.True(prepared.IsSuccess);

        var payloadDivergence = await ConfirmAsync(
            fixture, scenario, prepared.Value!, Guid.NewGuid(), Payload("""{"alvo":"outro"}"""));
        var policyDivergence = await ConfirmAsync(
            fixture, scenario, prepared.Value!, Guid.NewGuid(), payload, policyVersion: "1999-01-01.9");

        Assert.Equal("proposal_conflict", ErrorCode(payloadDivergence));
        Assert.Equal("proposal_conflict", ErrorCode(policyDivergence));
        await using var administrator = fixture.CreateAdministratorDbContext();
        var approval = await administrator.Approvals.SingleAsync();
        Assert.Null(approval.ConsumedAt);
        Assert.Equal(2, await administrator.AuditEvents.CountAsync(audit =>
            audit.Action == "proposal_conflict" && audit.Outcome == AuditOutcome.Denied));
    }

    [Fact]
    public async Task Confirming_with_a_divergent_proposal_identifier_is_a_conflict()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var payload = Payload("""{"alvo":"estoque"}""");
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), payload);
        Assert.True(prepared.IsSuccess);

        var result = await ConfirmAsync(
            fixture, scenario, prepared.Value!, Guid.NewGuid(), payload, proposalId: Guid.NewGuid());

        Assert.Equal("proposal_conflict", ErrorCode(result));
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Null((await administrator.Approvals.SingleAsync()).ConsumedAt);
    }

    [Fact]
    public async Task Confirming_an_unknown_approval_is_unavailable_without_mutation_or_denial_audit()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);

        var result = await ConfirmAsync(
            fixture,
            scenario,
            new PrepareApprovalResult(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)),
            Guid.NewGuid(),
            Payload("""{"alvo":"estoque"}"""));

        Assert.Equal("proposal_unavailable", ErrorCode(result));
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.AuditEvents.Where(audit =>
            audit.Outcome == AuditOutcome.Denied).ToListAsync());
    }

    [Fact]
    public async Task An_expired_proposal_cannot_be_confirmed()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var payload = Payload("""{"alvo":"estoque"}""");
        var prepared = await PrepareAsync(
            fixture, scenario, Guid.NewGuid(), payload, timeToLive: TimeSpan.FromSeconds(1));
        Assert.True(prepared.IsSuccess);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        var result = await ConfirmAsync(fixture, scenario, prepared.Value!, Guid.NewGuid(), payload);

        Assert.Equal("proposal_unavailable", ErrorCode(result));
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Null((await administrator.Approvals.SingleAsync()).ConsumedAt);
    }

    [Fact]
    public async Task Canonical_payload_key_ordering_matches_between_preparation_and_confirmation()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var prepared = await PrepareAsync(
            fixture, scenario, Guid.NewGuid(), Payload("""{"b":2,"a":1,"n":{"z":true,"y":[1,2]}}"""));
        Assert.True(prepared.IsSuccess);

        var result = await ConfirmAsync(
            fixture, scenario, prepared.Value!, Guid.NewGuid(),
            Payload("""{"a":1,"n":{"y":[1,2],"z":true},"b":2}"""));

        Assert.True(result.IsSuccess);
        Assert.Equal(ConfirmApprovalOutcome.Claimed, result.Value!.Outcome);
    }

    [Fact]
    public async Task Concurrent_confirmations_with_the_same_request_produce_one_claim_and_replays()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var payload = Payload("""{"alvo":"estoque"}""");
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), payload);
        Assert.True(prepared.IsSuccess);
        var confirmationRequestId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            ConfirmAsync(fixture, scenario, prepared.Value!, confirmationRequestId, payload)));

        Assert.Single(results, result => result.IsSuccess && result.Value!.Outcome == ConfirmApprovalOutcome.Claimed);
        Assert.Equal(7, results.Count(result => result.IsSuccess && result.Value!.Outcome == ConfirmApprovalOutcome.Replay));
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator.AuditEvents.CountAsync(audit =>
            audit.Action == "ai.approvals.confirm" && audit.Outcome == AuditOutcome.Success));
        Assert.Empty(await administrator.AuditEvents.Where(audit =>
            audit.Outcome == AuditOutcome.Denied).ToListAsync());
    }

    [Fact]
    public async Task Concurrent_confirmations_with_distinct_requests_produce_one_claim_and_audited_conflicts()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var payload = Payload("""{"alvo":"estoque"}""");
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), payload);
        Assert.True(prepared.IsSuccess);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            ConfirmAsync(fixture, scenario, prepared.Value!, Guid.NewGuid(), payload)));

        Assert.Single(results, result => result.IsSuccess && result.Value!.Outcome == ConfirmApprovalOutcome.Claimed);
        Assert.Equal(3, results.Count(result => ErrorCode(result) == "proposal_conflict"));
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator.AuditEvents.CountAsync(audit =>
            audit.Action == "ai.approvals.confirm" && audit.Outcome == AuditOutcome.Success));
        Assert.Equal(3, await administrator.AuditEvents.CountAsync(audit =>
            audit.Action == "proposal_conflict" && audit.Outcome == AuditOutcome.Denied));
    }

    [Fact]
    public async Task A_cross_company_confirmation_is_hidden_as_unavailable()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var payload = Payload("""{"alvo":"estoque"}""");
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), payload);
        Assert.True(prepared.IsSuccess);

        var result = await ConfirmAsync(
            fixture, scenario, prepared.Value!, Guid.NewGuid(), payload, companyId: scenario.OtherCompanyId);

        Assert.Equal("proposal_unavailable", ErrorCode(result));
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Null((await administrator.Approvals.SingleAsync()).ConsumedAt);
    }

    [Fact]
    public async Task A_cross_actor_confirmation_within_the_same_company_is_hidden_as_unavailable()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var payload = Payload("""{"alvo":"estoque"}""");
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), payload);
        Assert.True(prepared.IsSuccess);

        var result = await ConfirmAsync(
            fixture, scenario, prepared.Value!, Guid.NewGuid(), payload, actorUserId: scenario.OtherUserId);

        Assert.Equal("proposal_unavailable", ErrorCode(result));
        await using var administrator = fixture.CreateAdministratorDbContext();
        Assert.Null((await administrator.Approvals.SingleAsync()).ConsumedAt);
    }

    [Fact]
    public async Task A_runtime_query_without_session_context_is_fail_closed()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid());
        Assert.True(prepared.IsSuccess);

        await using var runtime = CreateRuntimeContext(fixture);
        Assert.Equal(0, await runtime.Approvals.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Vetoing_a_live_proposal_soft_deletes_it_with_a_terminal_reason_and_audits()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var payload = Payload("""{"alvo":"estoque"}""");
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), payload);
        Assert.True(prepared.IsSuccess);

        var veto = await VetoAsync(fixture, scenario, prepared.Value!, Guid.NewGuid());

        Assert.True(veto.IsSuccess);
        Assert.True(veto.Value!.WasVetoed);
        await using var administrator = fixture.CreateAdministratorDbContext();
        var approval = await administrator.Approvals.IgnoreQueryFilters().SingleAsync();
        Assert.NotNull(approval.DeletedAt);
        Assert.Equal(FoundationApproval.VetoReason, approval.DeletedReason);
        Assert.Equal(1, await administrator.AuditEvents.CountAsync(audit =>
            audit.Action == "ai.approvals.veto" && audit.Outcome == AuditOutcome.Success));

        var confirmation = await ConfirmAsync(fixture, scenario, prepared.Value!, Guid.NewGuid(), payload);
        Assert.Equal("proposal_unavailable", ErrorCode(confirmation));

        var preparation = await PrepareAsync(fixture, scenario, Guid.NewGuid());
        Assert.True(preparation.IsSuccess);
    }

    [Fact]
    public async Task Replaying_a_veto_is_idempotent_and_vetoing_a_consumed_approval_is_a_conflict()
    {
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture);
        var payload = Payload("""{"alvo":"estoque"}""");
        var vetoable = await PrepareAsync(fixture, scenario, Guid.NewGuid(), payload);
        Assert.True(vetoable.IsSuccess);
        var firstVeto = await VetoAsync(fixture, scenario, vetoable.Value!, Guid.NewGuid());
        Assert.True(firstVeto.IsSuccess);

        var replay = await VetoAsync(fixture, scenario, vetoable.Value!, Guid.NewGuid());

        Assert.True(replay.IsSuccess);
        Assert.False(replay.Value!.WasVetoed);

        var consumedProposal = await PrepareAsync(fixture, scenario, Guid.NewGuid(), payload);
        Assert.True(consumedProposal.IsSuccess);
        var claim = await ConfirmAsync(fixture, scenario, consumedProposal.Value!, Guid.NewGuid(), payload);
        Assert.True(claim.IsSuccess);

        var vetoAfterConsumption = await VetoAsync(fixture, scenario, consumedProposal.Value!, Guid.NewGuid());

        Assert.Equal("proposal_conflict", ErrorCode(vetoAfterConsumption));
    }

    private static async Task<FoundationPostgresFixture> StartFixtureAsync()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static FoundationDbContext CreateRuntimeContext(FoundationPostgresFixture fixture) =>
        new(new DbContextOptionsBuilder<FoundationDbContext>()
            .UseNpgsql(fixture.RuntimeConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new RowVersionInterceptor())
            .Options);

    private static PrepareApprovalService CreatePrepareService(FoundationDbContext database) =>
        new(
            database,
            new AuditAppender(database),
            new AuthorizationTelemetry(),
            new CapabilityEvaluator(database),
            NullLogger<PrepareApprovalService>.Instance);

    private static ConfirmApprovalService CreateConfirmService(FoundationDbContext database) =>
        new(
            database,
            new AuditAppender(database),
            new AuthorizationTelemetry(),
            new CapabilityEvaluator(database),
            NullLogger<ConfirmApprovalService>.Instance);

    private static async Task<AuthorizationResult<PrepareApprovalResult>> PrepareAsync(
        FoundationPostgresFixture fixture,
        ApprovalScenario scenario,
        Guid requestId,
        JsonElement? payload = null,
        TimeSpan? timeToLive = null,
        Guid? actorUserId = null)
    {
        await using var database = CreateRuntimeContext(fixture);
        var service = CreatePrepareService(database);
        return await service.PrepareAsync(
            new ActorContext(actorUserId ?? scenario.ActorUserId, Guid.NewGuid()),
            new PrepareApprovalCommand(
                scenario.CompanyId,
                requestId,
                Action,
                payload ?? Payload("""{"alvo":"estoque"}"""),
                PolicyVersion,
                timeToLive),
            CancellationToken.None);
    }

    private static async Task<AuthorizationResult<ConfirmApprovalResult>> ConfirmAsync(
        FoundationPostgresFixture fixture,
        ApprovalScenario scenario,
        PrepareApprovalResult proposal,
        Guid requestId,
        JsonElement payload,
        Guid? companyId = null,
        Guid? actorUserId = null,
        Guid? proposalId = null,
        string policyVersion = PolicyVersion)
    {
        await using var database = CreateRuntimeContext(fixture);
        var service = CreateConfirmService(database);
        return await service.ConfirmAsync(
            new ActorContext(actorUserId ?? scenario.ActorUserId, Guid.NewGuid()),
            new ConfirmApprovalCommand(
                companyId ?? scenario.CompanyId,
                proposal.ApprovalId,
                proposalId ?? proposal.ProposalId,
                requestId,
                payload,
                policyVersion),
            CancellationToken.None);
    }

    private static async Task<AuthorizationResult<VetoApprovalResult>> VetoAsync(
        FoundationPostgresFixture fixture,
        ApprovalScenario scenario,
        PrepareApprovalResult proposal,
        Guid requestId)
    {
        await using var database = CreateRuntimeContext(fixture);
        var service = CreateConfirmService(database);
        return await service.VetoAsync(
            new ActorContext(scenario.ActorUserId, Guid.NewGuid()),
            new VetoApprovalCommand(scenario.CompanyId, proposal.ApprovalId, proposal.ProposalId, requestId),
            CancellationToken.None);
    }

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string? ErrorCode<T>(AuthorizationResult<T> result) => result.ErrorCode?.ToStableCode();

    private static async Task<ApprovalScenario> SeedScenarioAsync(
        FoundationPostgresFixture fixture,
        bool grantCapability = true,
        CapabilityScope capabilityScope = CapabilityScope.Company)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var actorUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", actorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Alfa", actorUserId, requestId);
        var otherCompany = new Company(Guid.NewGuid(), group.Id, "Beta", actorUserId, requestId);
        var actor = new User(actorUserId, "Ana", "ana@example.test");
        var other = new User(otherUserId, "Beto", "beto@example.test");
        var actorMembership = new Membership(Guid.NewGuid(), company.Id, actorUserId, actorUserId, requestId);
        var otherMembership = new Membership(Guid.NewGuid(), company.Id, otherUserId, actorUserId, requestId);
        var otherCompanyMembership = new Membership(Guid.NewGuid(), otherCompany.Id, actorUserId, actorUserId, requestId);
        var role = new Role(Guid.NewGuid(), company.Id, "AI operator", actorUserId, requestId);
        var otherCompanyRole = new Role(Guid.NewGuid(), otherCompany.Id, "AI operator", actorUserId, requestId);
        var capability = await administrator.Capabilities.SingleAsync(candidate =>
            candidate.Name == KnownCapabilities.AiWritesConfirm);

        administrator.AddRange(
            group,
            company,
            otherCompany,
            actor,
            other,
            actorMembership,
            otherMembership,
            otherCompanyMembership,
            role,
            otherCompanyRole,
            new RoleAssignment(Guid.NewGuid(), company.Id, role.Id, actorMembership.Id, actorUserId, requestId),
            new RoleAssignment(Guid.NewGuid(), company.Id, role.Id, otherMembership.Id, actorUserId, requestId),
            new RoleAssignment(
                Guid.NewGuid(), otherCompany.Id, otherCompanyRole.Id, otherCompanyMembership.Id, actorUserId, requestId));
        if (grantCapability)
        {
            administrator.AddRange(
                new RoleCapability(Guid.NewGuid(), company.Id, role.Id, capability.Id, capabilityScope, actorUserId, requestId),
                new RoleCapability(
                    Guid.NewGuid(), otherCompany.Id, otherCompanyRole.Id, capability.Id, CapabilityScope.Company, actorUserId, requestId));
        }

        await administrator.SaveChangesAsync();
        return new ApprovalScenario(company.Id, otherCompany.Id, actorUserId, otherUserId);
    }

    private sealed record ApprovalScenario(
        Guid CompanyId,
        Guid OtherCompanyId,
        Guid ActorUserId,
        Guid OtherUserId);
}
