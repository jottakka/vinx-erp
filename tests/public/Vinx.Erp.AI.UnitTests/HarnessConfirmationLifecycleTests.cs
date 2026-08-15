using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.AI;
using Vinx.Erp.Foundation.Approvals;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class HarnessConfirmationLifecycleTests
{
    private static readonly Guid ActorId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid CompanyId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly VinxExecutionContext ServerContext = new(ActorId, CompanyId);
    private static readonly JsonElement Payload = JsonDocument.Parse("""{"target":"probe"}""").RootElement.Clone();

    [Fact]
    public async Task Concurrent_same_request_confirmations_start_one_execution_and_share_completion()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var pending = await PreparePendingWriteAsync(counter, new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"), AssistantText("done")));
        var durableCalls = 0;
        await using var coordinator = CreateCoordinator(
            clock,
            (_, _, approvalId, proposalId, _, _, _, _) =>
            {
                var outcome = Interlocked.Increment(ref durableCalls) == 1
                    ? ConfirmApprovalOutcome.Claimed
                    : ConfirmApprovalOutcome.Replay;
                return Task.FromResult(AuthorizationResult<ConfirmApprovalResult>.Success(
                    new ConfirmApprovalResult(approvalId, proposalId, outcome)));
            });
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());
        var confirmationId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => coordinator.ConfirmAsync(proposalId, confirmationId)));

        Assert.Equal(6, durableCalls);
        Assert.Equal(1, results.Count(result => result.Outcome == ConfirmApprovalOutcome.Claimed));
        Assert.Equal(5, results.Count(result => result.Outcome == ConfirmApprovalOutcome.Replay));
        Assert.All(results, result => Assert.NotNull(result.Response));
        Assert.Equal(1, counter.Count);
        Assert.Single(counter.RecordedRequestIds);
        Assert.Equal(1, coordinator.ActiveProposalCount);
    }

    [Fact]
    public async Task Commit_result_timeout_then_same_request_replay_adopts_recovered_ownership()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var pending = await PreparePendingWriteAsync(counter, new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"), AssistantText("recovered")));
        var committed = NewTcs<bool>();
        var durableCalls = 0;
        var receivedIds = new List<Guid>();
        var winnerId = Guid.Empty;
        await using var coordinator = CreateCoordinator(
            clock,
            async (_, _, approvalId, proposalId, confirmationRequestId, _, _, cancellationToken) =>
            {
                receivedIds.Add(confirmationRequestId);
                if (Interlocked.Increment(ref durableCalls) == 1)
                {
                    winnerId = confirmationRequestId;
                    committed.TrySetResult(true);
                    await WaitForCancellationAsync(cancellationToken);
                }

                return confirmationRequestId == winnerId
                    ? AuthorizationResult<ConfirmApprovalResult>.Success(new ConfirmApprovalResult(approvalId, proposalId, ConfirmApprovalOutcome.Replay))
                    : AuthorizationResult<ConfirmApprovalResult>.Failure(AuthorizationErrorCode.ProposalConflict);
            },
            durableCallTimeout: TimeSpan.FromSeconds(1));
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());
        var confirmationId = Guid.NewGuid();

        var first = coordinator.ConfirmAsync(proposalId, confirmationId);
        await committed.Task;
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => first);
        Assert.Equal(0, counter.Count);

        var replay = await coordinator.ConfirmAsync(proposalId, confirmationId);

        Assert.Equal(ConfirmApprovalOutcome.Replay, replay.Outcome);
        Assert.NotNull(replay.Response);
        Assert.Equal(1, counter.Count);
        Assert.Equal(2, durableCalls);
        Assert.Equal([confirmationId, confirmationId], receivedIds);
    }

    [Fact]
    public async Task Replay_can_adopt_before_original_claimed_caller_takes_local_lock()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var pending = await PreparePendingWriteAsync(counter, new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"), AssistantText("ordered")));
        var firstStarted = NewTcs<bool>();
        var releaseFirst = NewTcs<bool>();
        var call = 0;
        await using var coordinator = CreateCoordinator(
            clock,
            async (_, _, approvalId, proposalId, _, _, _, _) =>
            {
                if (Interlocked.Increment(ref call) == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task;
                    return AuthorizationResult<ConfirmApprovalResult>.Success(
                        new ConfirmApprovalResult(approvalId, proposalId, ConfirmApprovalOutcome.Claimed));
                }

                return AuthorizationResult<ConfirmApprovalResult>.Success(
                    new ConfirmApprovalResult(approvalId, proposalId, ConfirmApprovalOutcome.Replay));
            });
        var proposalId = Guid.NewGuid();
        var confirmationId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());

        var claimed = coordinator.ConfirmAsync(proposalId, confirmationId);
        await firstStarted.Task;
        var replay = await coordinator.ConfirmAsync(proposalId, confirmationId);
        releaseFirst.TrySetResult(true);
        var original = await claimed;

        Assert.Equal(ConfirmApprovalOutcome.Replay, replay.Outcome);
        Assert.Equal(ConfirmApprovalOutcome.Claimed, original.Outcome);
        Assert.Same(replay.Response, original.Response);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task Register_uses_registered_actor_and_correlation_for_durable_calls()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var pending = await PreparePendingWriteAsync(counter, new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"), AssistantText("done")));
        ActorContext? receivedActor = null;
        Guid receivedCompany = Guid.Empty;
        await using var coordinator = CreateCoordinator(
            clock,
            (actor, companyId, approvalId, proposalId, _, _, _, _) =>
            {
                receivedActor = actor;
                receivedCompany = companyId;
                return Task.FromResult(AuthorizationResult<ConfirmApprovalResult>.Success(
                    new ConfirmApprovalResult(approvalId, proposalId, ConfirmApprovalOutcome.Claimed)));
            });
        var proposalId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        coordinator.Register(
            proposalId,
            Guid.NewGuid(),
            correlationId,
            ServerContext,
            pending.Bundle,
            pending.Session,
            pending.Request,
            Payload,
            Guid.NewGuid(),
            clock.GetUtcNow() + TimeSpan.FromMinutes(5));

        await coordinator.ConfirmAsync(proposalId, Guid.NewGuid());

        var actor = Assert.IsType<ActorContext>(receivedActor);
        Assert.Equal(ActorId, actor.UserId);
        Assert.Equal(correlationId, actor.CorrelationId);
        Assert.Equal(CompanyId, receivedCompany);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task Different_request_after_unobserved_claim_cannot_adopt()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var pending = await PreparePendingWriteAsync(counter, new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"), AssistantText("must not execute")));
        var committed = NewTcs<bool>();
        var call = 0;
        var winnerId = Guid.Empty;
        var receivedIds = new List<Guid>();
        await using var coordinator = CreateCoordinator(
            clock,
            async (_, _, approvalId, proposalId, confirmationRequestId, _, _, cancellationToken) =>
            {
                receivedIds.Add(confirmationRequestId);
                if (Interlocked.Increment(ref call) == 1)
                {
                    winnerId = confirmationRequestId;
                    committed.TrySetResult(true);
                    await WaitForCancellationAsync(cancellationToken);
                }

                return confirmationRequestId == winnerId
                    ? AuthorizationResult<ConfirmApprovalResult>.Success(new ConfirmApprovalResult(approvalId, proposalId, ConfirmApprovalOutcome.Replay))
                    : AuthorizationResult<ConfirmApprovalResult>.Failure(AuthorizationErrorCode.ProposalConflict);
            },
            durableCallTimeout: TimeSpan.FromSeconds(1));
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());
        var firstId = Guid.NewGuid();
        var first = coordinator.ConfirmAsync(proposalId, firstId);
        await committed.Task;
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => first);

        var differentId = Guid.NewGuid();
        var conflict = await coordinator.ConfirmAsync(proposalId, differentId);

        Assert.Equal(ConfirmApprovalOutcome.Conflict, conflict.Outcome);
        Assert.Equal(AuthorizationErrorCode.ProposalConflict, conflict.ErrorCode);
        Assert.Equal(0, counter.Count);
        Assert.Equal([firstId, differentId], receivedIds);
    }

    [Fact]
    public async Task Caller_cancellation_after_claim_does_not_cancel_execution()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var client = new GatedChatClient();
        var pending = await PreparePendingWriteAsync(counter, client);
        var calls = 0;
        await using var coordinator = CreateCoordinator(
            clock,
            (_, _, approvalId, proposalId, _, _, _, _) => Task.FromResult(AuthorizationResult<ConfirmApprovalResult>.Success(
                new ConfirmApprovalResult(approvalId, proposalId,
                    Interlocked.Increment(ref calls) == 1 ? ConfirmApprovalOutcome.Claimed : ConfirmApprovalOutcome.Replay))));
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());
        using var callerCts = new CancellationTokenSource();
        var confirmationId = Guid.NewGuid();
        var first = coordinator.ConfirmAsync(proposalId, confirmationId, callerCts.Token);
        await client.DeliveryStarted.Task;
        callerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        client.Complete("execution completed");
        var replay = await coordinator.ConfirmAsync(proposalId, confirmationId);

        Assert.Equal(ConfirmApprovalOutcome.Replay, replay.Outcome);
        Assert.Equal("execution completed", replay.Response!.Text);
        Assert.Equal(1, counter.Count);
        Assert.False(client.CancellationObserved);
    }

    [Fact]
    public async Task Completion_wait_is_bounded_by_execution_deadline()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var client = new GatedChatClient();
        var pending = await PreparePendingWriteAsync(counter, client);
        await using var coordinator = CreateCoordinator(clock, executionTimeout: TimeSpan.FromSeconds(1));
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());

        var confirmation = coordinator.ConfirmAsync(proposalId, Guid.NewGuid());
        await client.DeliveryStarted.Task;
        clock.Advance(TimeSpan.FromSeconds(1));
        var exception = await Record.ExceptionAsync(() => confirmation);
        await client.DeliveryCompleted.Task;

        Assert.True(exception is OperationCanceledException or TimeoutException);
        Assert.True(client.CancellationObserved);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task Late_durable_success_after_shutdown_fails_closed_without_agent_delivery()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var client = new GatedChatClient();
        var pending = await PreparePendingWriteAsync(counter, client);
        var started = NewTcs<bool>();
        var release = NewTcs<bool>();
        await using var coordinator = CreateCoordinator(
            clock,
            async (_, _, approvalId, proposalId, _, _, _, _) =>
            {
                started.TrySetResult(true);
                await release.Task;
                return AuthorizationResult<ConfirmApprovalResult>.Success(
                    new ConfirmApprovalResult(approvalId, proposalId, ConfirmApprovalOutcome.Claimed));
            },
            shutdownDrainTimeout: TimeSpan.FromSeconds(1));
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());
        var confirmation = coordinator.ConfirmAsync(proposalId, Guid.NewGuid());
        await started.Task;

        var dispose = coordinator.DisposeAsync().AsTask();
        clock.Advance(TimeSpan.FromSeconds(1));
        await dispose;
        release.TrySetResult(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => confirmation);
        Assert.False(client.DeliveryStarted.Task.IsCompleted);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task Dispose_never_force_completes_attempt_drain()
    {
        var clock = new ManualTimeProvider();
        var pending = await PreparePendingWriteAsync(new HarnessProbeWriteCounter(), new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call")));
        var started = NewTcs<bool>();
        var release = NewTcs<bool>();
        await using var coordinator = CreateCoordinator(
            clock,
            async (_, _, approvalId, proposalId, _, _, _, _) =>
            {
                started.TrySetResult(true);
                await release.Task;
                return AuthorizationResult<ConfirmApprovalResult>.Success(
                    new ConfirmApprovalResult(approvalId, proposalId, ConfirmApprovalOutcome.Claimed));
            },
            shutdownDrainTimeout: TimeSpan.FromMinutes(1));
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());
        var confirmation = coordinator.ConfirmAsync(proposalId, Guid.NewGuid());
        await started.Task;

        var dispose = coordinator.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        release.TrySetResult(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => confirmation);
        await dispose;
    }

    [Fact]
    public async Task Shutdown_started_before_timer_quiescence_rejects_late_confirm_without_delivery()
    {
        var clock = new ManualTimeProvider();
        var timerGate = NewTcs<bool>();
        clock.GateNextTimerDispose(timerGate);
        var counter = new HarnessProbeWriteCounter();
        var client = new ScriptedChatClient(AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"), AssistantText("must not run"));
        var pending = await PreparePendingWriteAsync(counter, client);
        var committed = NewTcs<bool>();
        var release = NewTcs<bool>();
        await using var coordinator = CreateCoordinator(clock, async (_, _, approvalId, proposalId, _, _, _, _) =>
        {
            committed.TrySetResult(true);
            await release.Task;
            return AuthorizationResult<ConfirmApprovalResult>.Success(new ConfirmApprovalResult(approvalId, proposalId, ConfirmApprovalOutcome.Claimed));
        });
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());
        var confirmation = coordinator.ConfirmAsync(proposalId, Guid.NewGuid());
        await committed.Task;
        var dispose = coordinator.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        release.TrySetResult(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => confirmation);
        Assert.Single(client.Requests);
        Assert.Equal(0, counter.Count);
        timerGate.TrySetResult(true);
        await dispose;
    }

    [Fact]
    public async Task Shutdown_started_before_timer_quiescence_rejects_late_veto_without_delivery()
    {
        var clock = new ManualTimeProvider();
        var timerGate = NewTcs<bool>();
        clock.GateNextTimerDispose(timerGate);
        var counter = new HarnessProbeWriteCounter();
        var client = new ScriptedChatClient(AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"), AssistantText("must not veto"));
        var pending = await PreparePendingWriteAsync(counter, client);
        var committed = NewTcs<bool>();
        var release = NewTcs<bool>();
        await using var coordinator = CreateCoordinator(
            clock,
            vetoApproval: async (_, _, approvalId, proposalId, _, _) =>
            {
                committed.TrySetResult(true);
                await release.Task;
                return AuthorizationResult<VetoApprovalResult>.Success(
                    new VetoApprovalResult(approvalId, proposalId, VetoApprovalOutcome.Vetoed));
            });
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());
        var veto = coordinator.VetoAsync(proposalId, Guid.NewGuid());
        await committed.Task;

        var dispose = coordinator.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        release.TrySetResult(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => veto);
        Assert.Single(client.Requests);
        Assert.Equal(0, counter.Count);
        timerGate.TrySetResult(true);
        await dispose;
    }

    [Fact]
    public async Task Shutdown_winning_veto_handoff_never_starts_agent_delivery()
    {
        var clock = new ManualTimeProvider();
        var timerGate = NewTcs<bool>();
        clock.GateNextTimerDispose(timerGate);
        var counter = new HarnessProbeWriteCounter();
        var client = new GatedChatClient();
        var pending = await PreparePendingWriteAsync(counter, client);
        var coordinator = CreateCoordinator(clock);
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());
        var handoffReached = NewTcs<bool>();
        var releaseHandoff = NewTcs<bool>();
        clock.GateUtcNowAfterNextRead(handoffReached, releaseHandoff);

        var veto = Task.Run(() => coordinator.VetoAsync(proposalId, Guid.NewGuid()));
        await handoffReached.Task;
        var dispose = coordinator.DisposeAsync().AsTask();

        try
        {
            Assert.False(dispose.IsCompleted);
            releaseHandoff.TrySetResult(true);

            var next = await Task.WhenAny(veto, client.DeliveryStarted.Task);
            Assert.Same(veto, next);
            await Assert.ThrowsAsync<InvalidOperationException>(() => veto);
            Assert.False(client.DeliveryStarted.Task.IsCompleted);
            Assert.Equal(1, client.RequestCount);
            Assert.Equal(0, counter.Count);
        }
        finally
        {
            releaseHandoff.TrySetResult(true);
            timerGate.TrySetResult(true);
            await dispose;
        }
    }

    [Fact]
    public async Task Shutdown_winning_confirmation_handoff_never_starts_agent_delivery()
    {
        var clock = new ManualTimeProvider();
        var timerGate = NewTcs<bool>();
        clock.GateNextTimerDispose(timerGate);
        var counter = new HarnessProbeWriteCounter();
        var client = new GatedChatClient();
        var pending = await PreparePendingWriteAsync(counter, client);
        var coordinator = CreateCoordinator(clock);
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());
        var handoffReached = NewTcs<bool>();
        var releaseHandoff = NewTcs<bool>();
        clock.GateUtcNowAfterNextRead(handoffReached, releaseHandoff);

        var confirmation = Task.Run(() => coordinator.ConfirmAsync(proposalId, Guid.NewGuid()));
        await handoffReached.Task;
        var dispose = coordinator.DisposeAsync().AsTask();

        try
        {
            Assert.False(dispose.IsCompleted);
            releaseHandoff.TrySetResult(true);

            var next = await Task.WhenAny(confirmation, client.DeliveryStarted.Task);
            Assert.Same(confirmation, next);
            await Assert.ThrowsAsync<InvalidOperationException>(() => confirmation);
            Assert.False(client.DeliveryStarted.Task.IsCompleted);
            Assert.Equal(1, client.RequestCount);
            Assert.Equal(0, counter.Count);
        }
        finally
        {
            releaseHandoff.TrySetResult(true);
            timerGate.TrySetResult(true);
            await dispose;
        }
    }

    [Fact]
    public async Task Execution_deadline_before_start_completes_shared_terminal_state()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var client = new GatedChatClient();
        var pending = await PreparePendingWriteAsync(counter, client);
        await using var coordinator = CreateCoordinator(clock, executionTimeout: TimeSpan.FromSeconds(1));
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());
        var handoffReached = NewTcs<bool>();
        var releaseHandoff = NewTcs<bool>();
        clock.GateUtcNowAfterNextRead(handoffReached, releaseHandoff);

        var confirmation = Task.Run(() => coordinator.ConfirmAsync(proposalId, Guid.NewGuid()));
        await handoffReached.Task;
        clock.Advance(TimeSpan.FromSeconds(1));
        releaseHandoff.TrySetResult(true);

        await Assert.ThrowsAsync<TimeoutException>(() => confirmation);
        Assert.False(client.DeliveryStarted.Task.IsCompleted);
        Assert.Equal(1, client.RequestCount);
        Assert.Equal(0, counter.Count);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, coordinator.ActiveProposalCount);
    }

    [Fact]
    public async Task Sweep_publishes_cleanup_before_removal_so_dispose_observes_gated_resource_cleanup()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var client = new GatedChatClient();
        var pending = await PreparePendingWriteAsync(counter, client);
        var durableStarted = NewTcs<bool>();
        var releaseDurable = NewTcs<bool>();
        await using var coordinator = CreateCoordinator(
            clock,
            async (_, _, approvalId, currentProposalId, _, _, _, _) =>
            {
                durableStarted.TrySetResult(true);
                await releaseDurable.Task;
                return AuthorizationResult<ConfirmApprovalResult>.Success(
                    new ConfirmApprovalResult(approvalId, currentProposalId, ConfirmApprovalOutcome.Claimed));
            },
            sweepInterval: TimeSpan.FromSeconds(1),
            shutdownDrainTimeout: TimeSpan.FromMinutes(1));
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid(), TimeSpan.FromSeconds(1));

        var resourceCleanupGate = NewTcs<bool>();
        var confirmationTask = coordinator.ConfirmAsync(proposalId, Guid.NewGuid());
        await durableStarted.Task;
        clock.GateNextTimerDispose(resourceCleanupGate);
        releaseDurable.TrySetResult(true);
        await client.DeliveryStarted.Task;
        client.Complete("completed");
        var confirmation = await confirmationTask;
        Assert.Equal(ConfirmApprovalOutcome.Claimed, confirmation.Outcome);
        Assert.Equal(1, counter.Count);
        await client.DeliveryCompleted.Task;

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, coordinator.ActiveProposalCount);
        var dispose = coordinator.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        resourceCleanupGate.TrySetResult(true);
        await dispose;
    }

    [Fact]
    public async Task Concurrent_vetoes_share_one_denial_delivery()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var client = new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call"), AssistantText("vetoed"));
        var pending = await PreparePendingWriteAsync(counter, client);
        var calls = 0;
        await using var coordinator = CreateCoordinator(
            clock,
            vetoApproval: (_, _, approvalId, proposalId, _, _) => Task.FromResult(AuthorizationResult<VetoApprovalResult>.Success(
                new VetoApprovalResult(approvalId, proposalId,
                    Interlocked.Increment(ref calls) == 1 ? VetoApprovalOutcome.Vetoed : VetoApprovalOutcome.AlreadyVetoed))));
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => coordinator.VetoAsync(proposalId, Guid.NewGuid())));

        Assert.Equal(4, calls);
        Assert.All(results, result => Assert.Equal("vetoed", result.Response!.Text));
        Assert.Equal(0, counter.Count);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task Veto_delivery_failure_faults_all_waiters()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var client = new GatedChatClient();
        var pending = await PreparePendingWriteAsync(counter, client);
        await using var coordinator = CreateCoordinator(
            clock,
            vetoApproval: (_, _, approvalId, proposalId, _, _) => Task.FromResult(AuthorizationResult<VetoApprovalResult>.Success(
                new VetoApprovalResult(approvalId, proposalId, VetoApprovalOutcome.Vetoed))));
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());

        var first = coordinator.VetoAsync(proposalId, Guid.NewGuid());
        await client.DeliveryStarted.Task;
        var second = coordinator.VetoAsync(proposalId, Guid.NewGuid());
        client.Fail(new InvalidOperationException("delivery failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(0, counter.Count);
    }

    [Theory]
    [MemberData(nameof(AuthorizationCodes))]
    public async Task All_authorization_codes_map_exhaustively_for_confirm_and_veto(
        AuthorizationErrorCode code,
        ConfirmApprovalOutcome expectedConfirm,
        VetoApprovalOutcome expectedVeto,
        string stableCode)
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var pending = await PreparePendingWriteAsync(counter, new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call")));
        await using var coordinator = CreateCoordinator(
            clock,
            (_, _, _, _, _, _, _, _) => Task.FromResult(AuthorizationResult<ConfirmApprovalResult>.Failure(code)),
            (_, _, _, _, _, _) => Task.FromResult(AuthorizationResult<VetoApprovalResult>.Failure(code)));
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());

        var confirm = await coordinator.ConfirmAsync(proposalId, Guid.NewGuid());
        var veto = await coordinator.VetoAsync(proposalId, Guid.NewGuid());

        Assert.Equal(code, confirm.ErrorCode);
        Assert.Equal(code, veto.ErrorCode);
        Assert.Equal(expectedConfirm, confirm.Outcome);
        Assert.Equal(expectedVeto, veto.Outcome);
        Assert.Null(confirm.Response);
        Assert.Null(veto.Response);
        Assert.Equal(stableCode, code.ToStableCode());
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task Unknown_authorization_code_fails_closed()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var pending = await PreparePendingWriteAsync(counter, new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call")));
        await using var coordinator = CreateCoordinator(
            clock,
            (_, _, _, _, _, _, _, _) => Task.FromResult(AuthorizationResult<ConfirmApprovalResult>.Failure((AuthorizationErrorCode)999)),
            (_, _, _, _, _, _) => Task.FromResult(AuthorizationResult<VetoApprovalResult>.Failure((AuthorizationErrorCode)999)));
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => coordinator.ConfirmAsync(proposalId, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => coordinator.VetoAsync(proposalId, Guid.NewGuid()));
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task Register_binds_only_accepted_bundle_execution_request_id()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var accepted = await PreparePendingWriteAsync(counter, new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "accepted-call"), AssistantText("accepted")));
        var rejected = await PreparePendingWriteAsync(counter, new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "rejected-call")));
        var empty = await PreparePendingWriteAsync(counter, new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "empty-call")));
        await using var coordinator = CreateCoordinator(clock);
        var proposalId = Guid.NewGuid();
        var executionId = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() => rejected.Bundle.RequestIdSource.Resolve());
        Register(coordinator, accepted, proposalId, clock, executionId);
        Assert.True(accepted.Bundle.RequestIdSource.IsBound);
        Assert.Equal(executionId, accepted.Bundle.RequestIdSource.Resolve());
        Assert.Throws<InvalidOperationException>(() => Register(coordinator, rejected, proposalId, clock, Guid.NewGuid()));
        Assert.False(rejected.Bundle.RequestIdSource.IsBound);
        Assert.Throws<ArgumentException>(() => Register(coordinator, empty, Guid.NewGuid(), clock, Guid.Empty));
        Assert.False(empty.Bundle.RequestIdSource.IsBound);
        var result = await coordinator.ConfirmAsync(proposalId, Guid.NewGuid());

        Assert.Equal(ConfirmApprovalOutcome.Claimed, result.Outcome);
        Assert.Equal(1, counter.Count);
        Assert.Equal([executionId], counter.RecordedRequestIds);
    }

    [Fact]
    public async Task Dispose_quiesces_timer_cancels_work_completes_tcs_and_defers_shutdown_cleanup()
    {
        var clock = new ManualTimeProvider();
        var counter = new HarnessProbeWriteCounter();
        var pending = await PreparePendingWriteAsync(counter, new ScriptedChatClient(
            AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call")));
        var started = NewTcs<bool>();
        var release = NewTcs<bool>();
        await using var coordinator = CreateCoordinator(
            clock,
            async (_, _, approvalId, proposalId, _, _, _, cancellationToken) =>
            {
                started.TrySetResult(true);
                await release.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return AuthorizationResult<ConfirmApprovalResult>.Success(
                    new ConfirmApprovalResult(approvalId, proposalId, ConfirmApprovalOutcome.Claimed));
            },
            shutdownDrainTimeout: TimeSpan.FromSeconds(1));
        var proposalId = Guid.NewGuid();
        Register(coordinator, pending, proposalId, clock, Guid.NewGuid());
        var confirmation = coordinator.ConfirmAsync(proposalId, Guid.NewGuid());
        await started.Task;

        var dispose = coordinator.DisposeAsync().AsTask();
        clock.Advance(TimeSpan.FromSeconds(1));
        await dispose;
        await coordinator.DisposeAsync();
        Assert.Equal(0, coordinator.ActiveProposalCount);

        release.TrySetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirmation);
    }

    public static IEnumerable<object[]> AuthorizationCodes()
    {
        yield return [AuthorizationErrorCode.CompanyContextRequired, ConfirmApprovalOutcome.ProposalUnavailable, VetoApprovalOutcome.ProposalUnavailable, "company_context_required"];
        yield return [AuthorizationErrorCode.MembershipRequired, ConfirmApprovalOutcome.ProposalUnavailable, VetoApprovalOutcome.ProposalUnavailable, "membership_required"];
        yield return [AuthorizationErrorCode.CapabilityDenied, ConfirmApprovalOutcome.ProposalUnavailable, VetoApprovalOutcome.ProposalUnavailable, "capability_denied"];
        yield return [AuthorizationErrorCode.ResourceNotFound, ConfirmApprovalOutcome.ProposalUnavailable, VetoApprovalOutcome.ProposalUnavailable, "resource_not_found"];
        yield return [AuthorizationErrorCode.IdempotencyConflict, ConfirmApprovalOutcome.Conflict, VetoApprovalOutcome.Conflict, "idempotency_conflict"];
        yield return [AuthorizationErrorCode.IntegrityConflict, ConfirmApprovalOutcome.Conflict, VetoApprovalOutcome.Conflict, "integrity_conflict"];
        yield return [AuthorizationErrorCode.AuditUnavailable, ConfirmApprovalOutcome.ProposalUnavailable, VetoApprovalOutcome.ProposalUnavailable, "audit_unavailable"];
        yield return [AuthorizationErrorCode.ProposalUnavailable, ConfirmApprovalOutcome.ProposalUnavailable, VetoApprovalOutcome.ProposalUnavailable, "proposal_unavailable"];
        yield return [AuthorizationErrorCode.ProposalConflict, ConfirmApprovalOutcome.Conflict, VetoApprovalOutcome.Conflict, "proposal_conflict"];
    }

    private static HarnessConfirmationCoordinator CreateCoordinator(
        TimeProvider timeProvider,
        ConfirmApprovalDelegate? confirmApproval = null,
        VetoApprovalDelegate? vetoApproval = null,
        TimeSpan? sweepInterval = null,
        TimeSpan? durableCallTimeout = null,
        TimeSpan? executionTimeout = null,
        TimeSpan? vetoDeliveryTimeout = null,
        TimeSpan? shutdownDrainTimeout = null) =>
        new(
            confirmApproval ?? ((_, _, approvalId, proposalId, _, _, _, _) => Task.FromResult(
                AuthorizationResult<ConfirmApprovalResult>.Success(
                    new ConfirmApprovalResult(approvalId, proposalId, ConfirmApprovalOutcome.Claimed)))),
            vetoApproval ?? ((_, _, approvalId, proposalId, _, _) => Task.FromResult(
                AuthorizationResult<VetoApprovalResult>.Success(
                    new VetoApprovalResult(approvalId, proposalId, VetoApprovalOutcome.Vetoed)))),
            new HarnessApprovalGate(NullLogger<HarnessApprovalGate>.Instance),
            timeProvider,
            sweepInterval,
            durableCallTimeout,
            executionTimeout,
            vetoDeliveryTimeout,
            shutdownDrainTimeout);

    private static void Register(
        HarnessConfirmationCoordinator coordinator,
        PendingWrite pending,
        Guid proposalId,
        TimeProvider timeProvider,
        Guid executionRequestId,
        TimeSpan? ttl = null) =>
        coordinator.Register(
            proposalId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ServerContext,
            pending.Bundle,
            pending.Session,
            pending.Request,
            Payload,
            executionRequestId,
            timeProvider.GetUtcNow() + (ttl ?? TimeSpan.FromMinutes(5)));

    private static async Task<PendingWrite> PreparePendingWriteAsync(HarnessProbeWriteCounter counter, IChatClient client)
    {
        var bundle = VinxHarnessFactory.Create(client, counter);
        var session = await bundle.CreateSessionAsync();
        var response = await bundle.RunUserTurnAsync(session, "write probe");
        var request = response.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>().Single();
        return new PendingWrite(bundle, session, request);
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        var cancelled = NewTcs<bool>();
        using var registration = cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled);
        await cancelled.Task;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static TaskCompletionSource<T> NewTcs<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ChatMessage AssistantText(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage AssistantCall(string toolName, string callId) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, toolName)]);

    private sealed record PendingWrite(
        HarnessAgentBundle Bundle,
        AgentSession Session,
        ToolApprovalRequestContent Request);

    private sealed class GatedChatClient : IChatClient
    {
        private readonly TaskCompletionSource<ChatMessage> completion = NewTcs<ChatMessage>();
        private readonly bool ignoreCancellation;
        private int requestCount;

        public GatedChatClient(bool ignoreCancellation = false) => this.ignoreCancellation = ignoreCancellation;

        public TaskCompletionSource<bool> DeliveryStarted { get; } = NewTcs<bool>();

        public TaskCompletionSource<bool> DeliveryCompleted { get; } = NewTcs<bool>();

        public int RequestCount => Volatile.Read(ref requestCount);

        public bool CancellationObserved { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                return Task.FromResult(CreateResponse(AssistantCall(VinxToolPolicy.ProbeWriteToolName, "write-call")));
            }

            DeliveryStarted.TrySetResult(true);
            return AwaitCompletionAsync(cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Complete(string text) => completion.TrySetResult(AssistantText(text));

        public void Fail(Exception exception) => completion.TrySetException(exception);

        public void Dispose()
        {
        }

        private async Task<ChatResponse> AwaitCompletionAsync(CancellationToken cancellationToken)
        {
            if (ignoreCancellation)
            {
                var message = await completion.Task;
                DeliveryCompleted.TrySetResult(true);
                return CreateResponse(message);
            }

            try
            {
                var message = await completion.Task.WaitAsync(cancellationToken);
                DeliveryCompleted.TrySetResult(true);
                return CreateResponse(message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                DeliveryCompleted.TrySetResult(true);
                throw;
            }
        }

        private static ChatResponse CreateResponse(ChatMessage message) => new(message)
        {
            FinishReason = message.Contents.Any(content => content is FunctionCallContent)
                ? ChatFinishReason.ToolCalls
                : ChatFinishReason.Stop,
        };
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
        private long sequence;
        private TaskCompletionSource<bool>? nextDisposeGate;
        private TaskCompletionSource<bool>? utcNowGateReached;
        private TaskCompletionSource<bool>? utcNowGateRelease;
        private int utcNowReadsToSkip;

        public void GateNextTimerDispose(TaskCompletionSource<bool> gate)
        {
            lock (this.gate)
            {
                nextDisposeGate = gate;
            }
        }

        public void GateUtcNowAfterNextRead(
            TaskCompletionSource<bool> reached,
            TaskCompletionSource<bool> release)
        {
            lock (gate)
            {
                if (utcNowGateReached is not null)
                {
                    throw new InvalidOperationException("Já há uma leitura de relógio bloqueada.");
                }

                utcNowReadsToSkip = 1;
                utcNowGateReached = reached;
                utcNowGateRelease = release;
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            TaskCompletionSource<bool>? reached = null;
            TaskCompletionSource<bool>? release = null;
            DateTimeOffset current;
            lock (gate)
            {
                current = now;
                if (utcNowGateReached is not null)
                {
                    if (utcNowReadsToSkip > 0)
                    {
                        utcNowReadsToSkip--;
                    }
                    else
                    {
                        reached = utcNowGateReached;
                        release = utcNowGateRelease;
                        utcNowGateReached = null;
                        utcNowGateRelease = null;
                    }
                }
            }

            reached?.TrySetResult(true);
            release?.Task.GetAwaiter().GetResult();
            return current;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(dueTime));
            }

            if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(period));
            }

            lock (gate)
            {
                var timer = new ManualTimer(this, callback, state, NextDue(dueTime), dueTime, period, sequence++, nextDisposeGate);
                nextDisposeGate = null;
                timers.Add(timer);
                return timer;
            }
        }

        public void Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(delta));
            }

            lock (gate)
            {
                now += delta;
            }

            while (true)
            {
                ManualTimer? due;
                lock (gate)
                {
                    due = timers
                        .Where(timer => !timer.IsDisposed && timer.DueAt <= now)
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

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider owner;
            private TimeSpan period;
            private bool disposed;

            public ManualTimer(
                ManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                DateTimeOffset dueAt,
                TimeSpan dueTime,
                TimeSpan period,
                long sequence,
                TaskCompletionSource<bool>? disposeGate)
            {
                this.owner = owner;
                Callback = callback;
                State = state;
                DueAt = dueAt;
                this.period = period;
                Sequence = sequence;
                DisposeGate = disposeGate;
            }

            public TimerCallback Callback { get; }

            public object? State { get; }

            public DateTimeOffset DueAt { get; private set; }

            public long Sequence { get; }

            public bool IsDisposed => disposed;

            public TaskCompletionSource<bool>? DisposeGate { get; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed)
                {
                    return false;
                }

                this.period = period;
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
                return DisposeGate is null ? ValueTask.CompletedTask : new ValueTask(DisposeGate.Task);
            }

            public void ScheduleNext(DateTimeOffset now)
            {
                DueAt = period == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : now + period;
            }
        }
    }
}
