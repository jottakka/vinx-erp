using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.AI;
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

public sealed class ApprovalCoordinatorDurableLifecycleTests
{
    private const string PolicyVersion = "2026-08-14.1";
    private const string Action = "ai.writes.record";
    private static readonly JsonElement Payload = JsonDocument.Parse("""{"target":"inventory"}""").RootElement.Clone();

    [Fact]
    public async Task Concurrent_same_confirmation_request_reaches_foundation_each_time_and_executes_once()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture, timeout.Token);
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), timeout.Token);
        Assert.True(prepared.IsSuccess);
        var client = new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"),
            AssistantText("written"));
        var writes = new HarnessProbeWriteCounter();
        var pending = await PrepareHarnessAsync(client, writes, timeout.Token);
        var foundationOutcomes = new ConcurrentQueue<ConfirmApprovalOutcome>();
        var foundationCalls = 0;
        var correlationId = Guid.NewGuid();
        await using var coordinator = CreateCoordinator(
            fixture,
            confirmApproval: async (actor, companyId, approvalId, proposalId, requestId, payload, policyVersion, cancellationToken) =>
            {
                Interlocked.Increment(ref foundationCalls);
                var result = await ConfirmWithFreshContextAsync(
                    fixture, actor, companyId, approvalId, proposalId, requestId, payload, policyVersion, cancellationToken);
                if (result.Value is not null)
                {
                    foundationOutcomes.Enqueue(result.Value.Outcome);
                }

                return result;
            });
        var executionRequestId = Guid.NewGuid();
        Register(coordinator, scenario, prepared.Value!, pending, correlationId, executionRequestId);
        var confirmationRequestId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
                .Select(_ => coordinator.ConfirmAsync(prepared.Value!.ProposalId, confirmationRequestId, timeout.Token)))
            .WaitAsync(timeout.Token);

        Assert.Equal(6, foundationCalls);
        Assert.Equal(1, foundationOutcomes.Count(outcome => outcome == ConfirmApprovalOutcome.Claimed));
        Assert.Equal(5, foundationOutcomes.Count(outcome => outcome == ConfirmApprovalOutcome.Replay));
        Assert.Equal(1, results.Count(result => result.Outcome == ConfirmApprovalOutcome.Claimed));
        Assert.Equal(5, results.Count(result => result.Outcome == ConfirmApprovalOutcome.Replay));
        Assert.All(results, result => Assert.NotNull(result.Response));
        Assert.Equal(1, writes.Count);
        Assert.Equal([executionRequestId], writes.RecordedRequestIds);
        Assert.Equal(2, client.RequestCount);

        await using var administrator = fixture.CreateAdministratorDbContext();
        var approval = await administrator.Approvals.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.Id == prepared.Value!.ApprovalId, timeout.Token);
        Assert.Equal(scenario.CompanyId, approval.CompanyId);
        Assert.Equal(confirmationRequestId, approval.ConfirmationRequestId);
        var audit = Assert.Single(await administrator.AuditEvents.Where(candidate =>
            candidate.Action == "ai.approvals.confirm" && candidate.Outcome == AuditOutcome.Success)
            .ToListAsync(timeout.Token));
        Assert.Equal(scenario.CompanyId, audit.RequestedCompanyId);
        Assert.Equal(correlationId, audit.CorrelationId);
    }

    [Fact]
    public async Task Different_request_after_committed_unobserved_claim_is_conflict_and_cannot_execute()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture, timeout.Token);
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), timeout.Token);
        Assert.True(prepared.IsSuccess);
        var clock = new ManualTimeProvider();
        var committed = NewTcs<ConfirmApprovalOutcome>();
        var firstCall = 0;
        var client = new ScriptedChatClient(AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"));
        var writes = new HarnessProbeWriteCounter();
        var pending = await PrepareHarnessAsync(client, writes, timeout.Token);
        await using var coordinator = CreateCoordinator(
            fixture,
            clock,
            durableCallTimeout: TimeSpan.FromSeconds(1),
            confirmApproval: async (actor, companyId, approvalId, proposalId, requestId, payload, policyVersion, cancellationToken) =>
            {
                var result = await ConfirmWithFreshContextAsync(
                    fixture, actor, companyId, approvalId, proposalId, requestId, payload, policyVersion, cancellationToken);
                if (Interlocked.Increment(ref firstCall) == 1)
                {
                    committed.TrySetResult(Assert.IsType<ConfirmApprovalResult>(result.Value).Outcome);
                    await WaitForCancellationAsync(cancellationToken);
                }

                return result;
            });
        Register(coordinator, scenario, prepared.Value!, pending, Guid.NewGuid(), Guid.NewGuid());
        var winningRequestId = Guid.NewGuid();
        var lostResult = coordinator.ConfirmAsync(prepared.Value!.ProposalId, winningRequestId, timeout.Token);
        Assert.Equal(ConfirmApprovalOutcome.Claimed, await committed.Task.WaitAsync(timeout.Token));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => lostResult.WaitAsync(timeout.Token));

        var conflictingRequestId = Guid.NewGuid();
        var conflict = await coordinator.ConfirmAsync(
            prepared.Value!.ProposalId, conflictingRequestId, timeout.Token);

        Assert.Equal(ConfirmApprovalOutcome.Conflict, conflict.Outcome);
        Assert.Equal(AuthorizationErrorCode.ProposalConflict, conflict.ErrorCode);
        Assert.Null(conflict.Response);
        Assert.Equal(0, writes.Count);
        Assert.Equal(1, client.RequestCount);
        await using var administrator = fixture.CreateAdministratorDbContext();
        var approval = await administrator.Approvals.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.Id == prepared.Value!.ApprovalId, timeout.Token);
        Assert.Equal(scenario.CompanyId, approval.CompanyId);
        Assert.Equal(winningRequestId, approval.ConfirmationRequestId);
        Assert.Single(await administrator.AuditEvents.Where(candidate =>
            candidate.Action == "proposal_conflict" && candidate.Outcome == AuditOutcome.Denied)
            .ToListAsync(timeout.Token));
    }

    [Fact]
    public async Task Post_commit_timeout_recovers_only_same_request_with_replay_and_one_stable_execution()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture, timeout.Token);
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), timeout.Token);
        Assert.True(prepared.IsSuccess);
        var clock = new ManualTimeProvider();
        var committed = NewTcs<bool>();
        var durableOutcomes = new ConcurrentQueue<ConfirmApprovalOutcome>();
        var calls = 0;
        var client = new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"),
            AssistantText("recovered"));
        var writes = new HarnessProbeWriteCounter();
        var pending = await PrepareHarnessAsync(client, writes, timeout.Token);
        var correlationId = Guid.NewGuid();
        await using var coordinator = CreateCoordinator(
            fixture,
            clock,
            durableCallTimeout: TimeSpan.FromSeconds(1),
            confirmApproval: async (actor, companyId, approvalId, proposalId, requestId, payload, policyVersion, cancellationToken) =>
            {
                var result = await ConfirmWithFreshContextAsync(
                    fixture, actor, companyId, approvalId, proposalId, requestId, payload, policyVersion, cancellationToken);
                durableOutcomes.Enqueue(Assert.IsType<ConfirmApprovalResult>(result.Value).Outcome);
                if (Interlocked.Increment(ref calls) == 1)
                {
                    committed.TrySetResult(true);
                    await WaitForCancellationAsync(cancellationToken);
                }

                return result;
            });
        var executionRequestId = Guid.NewGuid();
        Register(coordinator, scenario, prepared.Value!, pending, correlationId, executionRequestId);
        var confirmationRequestId = Guid.NewGuid();
        var first = coordinator.ConfirmAsync(prepared.Value!.ProposalId, confirmationRequestId, timeout.Token);
        await committed.Task.WaitAsync(timeout.Token);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(timeout.Token));

        var replay = await coordinator.ConfirmAsync(
            prepared.Value!.ProposalId, confirmationRequestId, timeout.Token);

        Assert.Equal(ConfirmApprovalOutcome.Replay, replay.Outcome);
        Assert.NotNull(replay.Response);
        Assert.Equal(2, calls);
        Assert.Equal([ConfirmApprovalOutcome.Claimed, ConfirmApprovalOutcome.Replay], durableOutcomes);
        Assert.Equal(1, writes.Count);
        Assert.Equal([executionRequestId], writes.RecordedRequestIds);
        Assert.Equal(2, client.RequestCount);
        await using var administrator = fixture.CreateAdministratorDbContext();
        var approval = await administrator.Approvals.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.Id == prepared.Value!.ApprovalId, timeout.Token);
        Assert.Equal(confirmationRequestId, approval.ConfirmationRequestId);
        var audit = Assert.Single(await administrator.AuditEvents.Where(candidate =>
            candidate.Action == "ai.approvals.confirm" && candidate.Outcome == AuditOutcome.Success)
            .ToListAsync(timeout.Token));
        Assert.Equal(correlationId, audit.CorrelationId);
    }

    [Fact]
    public async Task Durable_veto_is_delivered_once_and_persists_terminal_audit_without_write()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture, timeout.Token);
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), timeout.Token);
        Assert.True(prepared.IsSuccess);
        var client = new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"),
            AssistantText("vetoed"));
        var writes = new HarnessProbeWriteCounter();
        var pending = await PrepareHarnessAsync(client, writes, timeout.Token);
        var correlationId = Guid.NewGuid();
        await using var coordinator = CreateCoordinator(fixture);
        Register(coordinator, scenario, prepared.Value!, pending, correlationId, Guid.NewGuid());

        var vetoed = await coordinator.VetoAsync(prepared.Value!.ProposalId, Guid.NewGuid(), timeout.Token);
        var replay = await coordinator.VetoAsync(prepared.Value!.ProposalId, Guid.NewGuid(), timeout.Token);

        Assert.Equal(VetoApprovalOutcome.Vetoed, vetoed.Outcome);
        Assert.Equal(VetoApprovalOutcome.AlreadyVetoed, replay.Outcome);
        Assert.NotNull(vetoed.Response);
        Assert.NotNull(replay.Response);
        Assert.Equal(0, writes.Count);
        Assert.Equal(2, client.RequestCount);
        await using var administrator = fixture.CreateAdministratorDbContext();
        var approval = await administrator.Approvals.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.Id == prepared.Value!.ApprovalId, timeout.Token);
        Assert.Equal(scenario.CompanyId, approval.CompanyId);
        Assert.True(approval.IsVetoed);
        Assert.Equal(FoundationApproval.VetoReason, approval.DeletedReason);
        var audit = Assert.Single(await administrator.AuditEvents.Where(candidate =>
            candidate.Action == "ai.approvals.veto" && candidate.Outcome == AuditOutcome.Success)
            .ToListAsync(timeout.Token));
        Assert.Equal(correlationId, audit.CorrelationId);
        Assert.Equal(scenario.CompanyId, audit.RequestedCompanyId);
    }

    [Fact]
    public async Task Membership_revocation_before_same_request_replay_is_reauthorized_without_cached_execution()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture, timeout.Token);
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), timeout.Token);
        Assert.True(prepared.IsSuccess);
        var client = new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"),
            AssistantText("written"));
        var writes = new HarnessProbeWriteCounter();
        var pending = await PrepareHarnessAsync(client, writes, timeout.Token);
        var correlationId = Guid.NewGuid();
        await using var coordinator = CreateCoordinator(fixture);
        Register(coordinator, scenario, prepared.Value!, pending, correlationId, Guid.NewGuid());
        var confirmationRequestId = Guid.NewGuid();
        var first = await coordinator.ConfirmAsync(prepared.Value!.ProposalId, confirmationRequestId, timeout.Token);
        Assert.Equal(ConfirmApprovalOutcome.Claimed, first.Outcome);

        await RevokeMembershipAsync(fixture, scenario, timeout.Token);
        var replay = await coordinator.ConfirmAsync(prepared.Value!.ProposalId, confirmationRequestId, timeout.Token);

        Assert.Equal(ConfirmApprovalOutcome.ProposalUnavailable, replay.Outcome);
        Assert.Equal(AuthorizationErrorCode.MembershipRequired, replay.ErrorCode);
        Assert.Null(replay.Response);
        Assert.Equal(1, writes.Count);
        Assert.Equal(2, client.RequestCount);
        await AssertDeniedAuditAsync(
            fixture, "membership_required", scenario.CompanyId, correlationId, timeout.Token);
    }

    [Fact]
    public async Task Capability_revocation_before_same_request_replay_is_reauthorized_without_cached_execution()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await StartFixtureAsync();
        var scenario = await SeedScenarioAsync(fixture, timeout.Token);
        var prepared = await PrepareAsync(fixture, scenario, Guid.NewGuid(), timeout.Token);
        Assert.True(prepared.IsSuccess);
        var client = new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"),
            AssistantText("written"));
        var writes = new HarnessProbeWriteCounter();
        var pending = await PrepareHarnessAsync(client, writes, timeout.Token);
        var correlationId = Guid.NewGuid();
        await using var coordinator = CreateCoordinator(fixture);
        Register(coordinator, scenario, prepared.Value!, pending, correlationId, Guid.NewGuid());
        var confirmationRequestId = Guid.NewGuid();
        var first = await coordinator.ConfirmAsync(prepared.Value!.ProposalId, confirmationRequestId, timeout.Token);
        Assert.Equal(ConfirmApprovalOutcome.Claimed, first.Outcome);

        await RevokeCapabilityAsync(fixture, scenario, timeout.Token);
        var replay = await coordinator.ConfirmAsync(prepared.Value!.ProposalId, confirmationRequestId, timeout.Token);

        Assert.Equal(ConfirmApprovalOutcome.ProposalUnavailable, replay.Outcome);
        Assert.Equal(AuthorizationErrorCode.CapabilityDenied, replay.ErrorCode);
        Assert.Null(replay.Response);
        Assert.Equal(1, writes.Count);
        Assert.Equal(2, client.RequestCount);
        await AssertDeniedAuditAsync(
            fixture, "capability_denied", scenario.CompanyId, correlationId, timeout.Token);
    }

    private static async Task<FoundationPostgresFixture> StartFixtureAsync()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static HarnessConfirmationCoordinator CreateCoordinator(
        FoundationPostgresFixture fixture,
        TimeProvider? timeProvider = null,
        TimeSpan? durableCallTimeout = null,
        ConfirmApprovalDelegate? confirmApproval = null,
        VetoApprovalDelegate? vetoApproval = null) =>
        new(
            confirmApproval ?? ((actor, companyId, approvalId, proposalId, requestId, payload, policyVersion, cancellationToken) =>
                ConfirmWithFreshContextAsync(
                    fixture, actor, companyId, approvalId, proposalId, requestId, payload, policyVersion, cancellationToken)),
            vetoApproval ?? ((actor, companyId, approvalId, proposalId, requestId, cancellationToken) =>
                VetoWithFreshContextAsync(fixture, actor, companyId, approvalId, proposalId, requestId, cancellationToken)),
            new HarnessApprovalGate(NullLogger<HarnessApprovalGate>.Instance),
            timeProvider,
            sweepInterval: TimeSpan.FromMinutes(30),
            durableCallTimeout: durableCallTimeout,
            logger: NullLogger<HarnessConfirmationCoordinator>.Instance);

    private static void Register(
        HarnessConfirmationCoordinator coordinator,
        ApprovalScenario scenario,
        PrepareApprovalResult prepared,
        PendingHarness pending,
        Guid correlationId,
        Guid executionRequestId) =>
        coordinator.Register(
            prepared.ProposalId,
            prepared.ApprovalId,
            correlationId,
            new VinxExecutionContext(scenario.ActorUserId, scenario.CompanyId),
            pending.Bundle,
            pending.Session,
            pending.Request,
            Payload,
            executionRequestId,
            prepared.ExpiresAt);

    private static async Task<PendingHarness> PrepareHarnessAsync(
        ScriptedChatClient client,
        HarnessProbeWriteCounter writes,
        CancellationToken cancellationToken)
    {
        var bundle = VinxHarnessFactory.Create(client, writes);
        var session = await bundle.CreateSessionAsync(cancellationToken);
        var response = await bundle.RunUserTurnAsync(session, "write inventory", cancellationToken);
        var request = response.Messages.SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>()
            .Single();
        return new PendingHarness(bundle, session, request);
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
        CancellationToken cancellationToken)
    {
        await using var database = CreateRuntimeContext(fixture);
        return await CreatePrepareService(database).PrepareAsync(
            new ActorContext(scenario.ActorUserId, Guid.NewGuid()),
            new PrepareApprovalCommand(scenario.CompanyId, requestId, Action, Payload, PolicyVersion),
            cancellationToken);
    }

    private static async Task<AuthorizationResult<ConfirmApprovalResult>> ConfirmWithFreshContextAsync(
        FoundationPostgresFixture fixture,
        ActorContext actor,
        Guid companyId,
        Guid approvalId,
        Guid proposalId,
        Guid requestId,
        JsonElement payload,
        string policyVersion,
        CancellationToken cancellationToken)
    {
        await using var database = CreateRuntimeContext(fixture);
        return await CreateConfirmService(database).ConfirmAsync(
            actor,
            new ConfirmApprovalCommand(companyId, approvalId, proposalId, requestId, payload, policyVersion),
            cancellationToken);
    }

    private static async Task<AuthorizationResult<VetoApprovalResult>> VetoWithFreshContextAsync(
        FoundationPostgresFixture fixture,
        ActorContext actor,
        Guid companyId,
        Guid approvalId,
        Guid proposalId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var database = CreateRuntimeContext(fixture);
        return await CreateConfirmService(database).VetoAsync(
            actor,
            new VetoApprovalCommand(companyId, approvalId, proposalId, requestId),
            cancellationToken);
    }

    private static async Task RevokeMembershipAsync(
        FoundationPostgresFixture fixture,
        ApprovalScenario scenario,
        CancellationToken cancellationToken)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var membership = await administrator.Memberships.SingleAsync(candidate =>
            candidate.CompanyId == scenario.CompanyId && candidate.UserId == scenario.ActorUserId,
            cancellationToken);
        membership.Revoke("test_revocation", scenario.ActorUserId, Guid.NewGuid());
        await administrator.SaveChangesAsync(cancellationToken);
    }

    private static async Task RevokeCapabilityAsync(
        FoundationPostgresFixture fixture,
        ApprovalScenario scenario,
        CancellationToken cancellationToken)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var grant = await administrator.RoleCapabilities.SingleAsync(candidate =>
            candidate.CompanyId == scenario.CompanyId && candidate.CapabilityId == KnownCapabilities.AiWritesConfirmId,
            cancellationToken);
        administrator.Remove(grant);
        await administrator.SaveChangesAsync(cancellationToken);
    }

    private static async Task AssertDeniedAuditAsync(
        FoundationPostgresFixture fixture,
        string action,
        Guid companyId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var audit = Assert.Single(await administrator.AuditEvents.Where(candidate =>
            candidate.Action == action && candidate.Outcome == AuditOutcome.Denied)
            .ToListAsync(cancellationToken));
        Assert.Equal(companyId, audit.RequestedCompanyId);
        Assert.Equal(correlationId, audit.CorrelationId);
    }

    private static async Task<ApprovalScenario> SeedScenarioAsync(
        FoundationPostgresFixture fixture,
        CancellationToken cancellationToken)
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
            candidate.Name == KnownCapabilities.AiWritesConfirm, cancellationToken);

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
            new RoleAssignment(Guid.NewGuid(), otherCompany.Id, otherCompanyRole.Id, otherCompanyMembership.Id, actorUserId, requestId),
            new RoleCapability(Guid.NewGuid(), company.Id, role.Id, capability.Id, CapabilityScope.Company, actorUserId, requestId),
            new RoleCapability(Guid.NewGuid(), otherCompany.Id, otherCompanyRole.Id, capability.Id, CapabilityScope.Company, actorUserId, requestId));
        await administrator.SaveChangesAsync(cancellationToken);
        return new ApprovalScenario(company.Id, otherCompany.Id, actorUserId);
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        var cancelled = NewTcs<bool>();
        using var registration = cancellationToken.Register(static state =>
            ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled);
        await cancelled.Task;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static TaskCompletionSource<T> NewTcs<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ChatMessage AssistantText(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage AssistantCall(string toolName, string callId) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, toolName)]);

    private sealed record PendingHarness(
        HarnessAgentBundle Bundle,
        AgentSession Session,
        ToolApprovalRequestContent Request);

    private sealed record ApprovalScenario(Guid CompanyId, Guid OtherCompanyId, Guid ActorUserId);

    private sealed class ScriptedChatClient(params ChatMessage[] responses) : IChatClient
    {
        private readonly Queue<ChatMessage> responses = new(responses);
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref requestCount);
            if (!responses.TryDequeue(out var message))
            {
                throw new InvalidOperationException("O roteiro do cliente de chat foi consumido inesperadamente.");
            }

            return Task.FromResult(new ChatResponse(message)
            {
                FinishReason = message.Contents.Any(content => content is FunctionCallContent)
                    ? ChatFinishReason.ToolCalls
                    : ChatFinishReason.Stop,
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        private long sequence;

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate)
            {
                return now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate)
            {
                var timer = new ManualTimer(this, callback, state, NextDue(dueTime), period, sequence++);
                timers.Add(timer);
                return timer;
            }
        }

        public void Advance(TimeSpan delta)
        {
            lock (gate)
            {
                now += delta;
            }

            while (true)
            {
                ManualTimer? due;
                lock (gate)
                {
                    due = timers.Where(timer => !timer.IsDisposed && timer.DueAt <= now)
                        .OrderBy(timer => timer.DueAt)
                        .ThenBy(timer => timer.Sequence)
                        .FirstOrDefault();
                    if (due is null)
                    {
                        return;
                    }

                    due.ScheduleNext(now);
                }

                due.Callback(due.State);
            }
        }

        private DateTimeOffset NextDue(TimeSpan dueTime) =>
            dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now + dueTime;

        private void Remove(ManualTimer timer)
        {
            lock (gate)
            {
                timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            DateTimeOffset dueAt,
            TimeSpan period,
            long sequence) : ITimer
        {
            private bool disposed;

            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public DateTimeOffset DueAt { get; private set; } = dueAt;
            public TimeSpan Period { get; private set; } = period;
            public long Sequence { get; } = sequence;
            public bool IsDisposed => disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed)
                {
                    return false;
                }

                Period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : owner.GetUtcNow() + dueTime;
                return true;
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void ScheduleNext(DateTimeOffset currentTime) =>
                DueAt = Period == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : currentTime + Period;
        }
    }
}
