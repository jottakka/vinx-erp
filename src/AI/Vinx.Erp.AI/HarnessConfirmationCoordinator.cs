using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.Foundation.Approvals;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.AI;

/// <summary>
/// Delegado que executa a confirmação durável na Foundation carregando o
/// <see cref="ActorContext"/> confiável. O coordenador é agnóstico de banco de
/// dados: em produção aponta para <c>ConfirmApprovalService</c>; em testes
/// recebe um duplo determinístico. O resultado é o
/// <see cref="AuthorizationResult{T}"/> completo — nenhum código de autorização
/// é colapsado na fronteira.
/// </summary>
public delegate Task<AuthorizationResult<ConfirmApprovalResult>> ConfirmApprovalDelegate(
    ActorContext actor,
    Guid companyId,
    Guid approvalId,
    Guid proposalId,
    Guid confirmationRequestId,
    JsonElement payload,
    string policyVersion,
    CancellationToken cancellationToken);

/// <summary>
/// Delegado que executa o veto durável na Foundation carregando o
/// <see cref="ActorContext"/> confiável e devolvendo o
/// <see cref="AuthorizationResult{T}"/> completo.
/// </summary>
public delegate Task<AuthorizationResult<VetoApprovalResult>> VetoApprovalDelegate(
    ActorContext actor,
    Guid companyId,
    Guid approvalId,
    Guid proposalId,
    Guid vetoRequestId,
    CancellationToken cancellationToken);

/// <summary>
/// Resultado da confirmação no coordenador. <see cref="Response"/> só existe
/// para <see cref="ConfirmApprovalOutcome.Claimed"/> (execução do vencedor) e
/// <see cref="ConfirmApprovalOutcome.Replay"/> (resultado reutilizado do
/// vencedor); conflitos e indisponibilidade nunca executam.
/// <see cref="ErrorCode"/> preserva o código durável exato da Foundation.
/// </summary>
public sealed record HarnessConfirmationResult(
    ConfirmApprovalOutcome Outcome,
    AgentResponse? Response,
    AuthorizationErrorCode? ErrorCode);

/// <summary>Resultado do veto no coordenador, com código durável exato.</summary>
public sealed record HarnessVetoResult(
    VetoApprovalOutcome Outcome,
    AgentResponse? Response,
    AuthorizationErrorCode? ErrorCode);

/// <summary>
/// Coordenador de confirmações do harness (código do servidor). Possui o
/// agente, a sessão e a solicitação pendente por <c>proposalId</c> e garante
/// single-flight: toda confirmação/veto alcança a Foundation (autorização
/// repetida inclusive em replay); somente o vencedor do claim durável retoma a
/// sessão, uma única vez, por tarefa de execução possuída pelo coordenador.
/// Um claim durável commitado mas não observado é recuperado apenas pelo mesmo
/// <c>RequestId</c> de confirmação (replay durável adota a posse local
/// atomicamente). Entradas terminais são retidas até a expiração para replays
/// dentro do TTL; uma varredura periódica/oportunista as remove com segurança
/// referencial. Após reinício do processo a proposta viva é perdida
/// (<see cref="ConfirmApprovalOutcome.ProposalUnavailable"/>, sem promessa de
/// replay de <see cref="AgentResponse"/>); o cliente re-propõe com o mesmo
/// <c>RequestId</c> de execução estável.
/// </summary>
public sealed class HarnessConfirmationCoordinator : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, PendingProposal> proposals = new();
    private readonly HashSet<Task> ownedCleanupTasks = [];
    private readonly ConfirmApprovalDelegate confirmApproval;
    private readonly VetoApprovalDelegate vetoApproval;
    private readonly HarnessApprovalGate approvalGate;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan sweepInterval;
    private readonly TimeSpan durableCallTimeout;
    private readonly TimeSpan executionTimeout;
    private readonly TimeSpan vetoDeliveryTimeout;
    private readonly TimeSpan shutdownDrainTimeout;
    private readonly ILogger<HarnessConfirmationCoordinator> logger;
    private readonly CancellationTokenSource shutdownCts = new();
    private readonly ITimer sweepTimer;
    private bool isStopping;
    private int disposeState;
    private int shutdownCtsDisposed;
    private Task? deferredShutdownCleanup;

    public HarnessConfirmationCoordinator(
        ConfirmApprovalDelegate confirmApproval,
        VetoApprovalDelegate vetoApproval,
        HarnessApprovalGate approvalGate,
        TimeProvider? timeProvider = null,
        TimeSpan? sweepInterval = null,
        TimeSpan? durableCallTimeout = null,
        TimeSpan? executionTimeout = null,
        TimeSpan? vetoDeliveryTimeout = null,
        TimeSpan? shutdownDrainTimeout = null,
        ILogger<HarnessConfirmationCoordinator>? logger = null)
    {
        this.confirmApproval = confirmApproval ?? throw new ArgumentNullException(nameof(confirmApproval));
        this.vetoApproval = vetoApproval ?? throw new ArgumentNullException(nameof(vetoApproval));
        this.approvalGate = approvalGate ?? throw new ArgumentNullException(nameof(approvalGate));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.sweepInterval = RequirePositive(sweepInterval ?? TimeSpan.FromMinutes(1), nameof(sweepInterval));
        this.durableCallTimeout = RequirePositive(durableCallTimeout ?? TimeSpan.FromSeconds(15), nameof(durableCallTimeout));
        this.executionTimeout = RequirePositive(executionTimeout ?? TimeSpan.FromMinutes(5), nameof(executionTimeout));
        this.vetoDeliveryTimeout = RequirePositive(vetoDeliveryTimeout ?? TimeSpan.FromMinutes(1), nameof(vetoDeliveryTimeout));
        this.shutdownDrainTimeout = RequirePositive(shutdownDrainTimeout ?? TimeSpan.FromSeconds(30), nameof(shutdownDrainTimeout));
        this.logger = logger ?? NullLogger<HarnessConfirmationCoordinator>.Instance;
        sweepTimer = this.timeProvider.CreateTimer(
            _ =>
            {
                try
                {
                    Sweep();
                }
                catch (Exception exception)
                {
                    this.logger.LogWarning(exception, "Varredura de propostas expiradas falhou de forma segura.");
                }
            },
            null,
            this.sweepInterval,
            this.sweepInterval);
    }

    /// <summary>Propostas mantidas em processo (somente para diagnóstico/testes).</summary>
    public int ActiveProposalCount
    {
        get
        {
            lock (gate)
            {
                return proposals.Count;
            }
        }
    }

    /// <summary>
    /// Registra uma proposta já autorizada e com solicitação de aprovação
    /// pendente. Sob um único lock: validação → checagem de duplicidade →
    /// vínculo do <c>RequestId</c> de execução na fonte do feixe → inserção.
    /// Falha de vínculo não deixa estado registrado.
    /// </summary>
    public void Register(
        Guid proposalId,
        Guid approvalId,
        Guid correlationId,
        VinxExecutionContext context,
        HarnessAgentBundle bundle,
        AgentSession session,
        ToolApprovalRequestContent pendingRequest,
        JsonElement payload,
        Guid executionRequestId,
        DateTimeOffset expiresAt)
    {
        if (proposalId == Guid.Empty)
        {
            throw new ArgumentException("O proposalId não pode ser vazio.", nameof(proposalId));
        }

        if (approvalId == Guid.Empty)
        {
            throw new ArgumentException("O approvalId não pode ser vazio.", nameof(approvalId));
        }

        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException("O correlationId não pode ser vazio.", nameof(correlationId));
        }

        if (executionRequestId == Guid.Empty)
        {
            throw new ArgumentException("O RequestId de execução não pode ser vazio.", nameof(executionRequestId));
        }

        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pendingRequest);

        if (!context.IsValid)
        {
            throw new ArgumentException("O contexto de execução confiável é obrigatório.", nameof(context));
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("O payload da proposta deve ser um objeto JSON.", nameof(payload));
        }

        if (expiresAt <= timeProvider.GetUtcNow())
        {
            throw new ArgumentException("A expiração da proposta deve ser futura.", nameof(expiresAt));
        }

        var callId = (pendingRequest.ToolCall as FunctionCallContent)?.CallId;
        if (string.IsNullOrWhiteSpace(callId))
        {
            throw new ArgumentException("A solicitação pendente deve ter CallId.", nameof(pendingRequest));
        }

        var entry = new PendingProposal
        {
            ProposalId = proposalId,
            ApprovalId = approvalId,
            CorrelationId = correlationId,
            Context = context,
            Bundle = bundle,
            Session = session,
            Request = pendingRequest,
            CallId = callId,
            Payload = payload,
            ExecutionRequestId = executionRequestId,
            ExpiresAt = expiresAt,
        };

        lock (gate)
        {
            if (isStopping)
            {
                throw new InvalidOperationException("O coordenador está encerrando; registro recusado.");
            }

            if (proposals.ContainsKey(proposalId))
            {
                throw new InvalidOperationException(
                    $"Já existe proposta viva registrada para o proposalId '{proposalId}'.");
            }

            bundle.RequestIdSource.Bind(executionRequestId);
            proposals.Add(proposalId, entry);
        }
    }

    /// <summary>
    /// Confirma a proposta: a Foundation decide (autorização repetida, claim
    /// single-use, replay por <c>RequestId</c>); somente o vencedor retoma a
    /// sessão, uma vez. Replay durável com estado local ausente adota a posse
    /// recuperada (claim commitado não observado).
    /// </summary>
    public async Task<HarnessConfirmationResult> ConfirmAsync(
        Guid proposalId,
        Guid confirmationRequestId,
        CancellationToken cancellationToken = default)
    {
        if (proposalId == Guid.Empty)
        {
            throw new ArgumentException("O proposalId não pode ser vazio.", nameof(proposalId));
        }

        if (confirmationRequestId == Guid.Empty)
        {
            throw new ArgumentException("O RequestId de confirmação não pode ser vazio.", nameof(confirmationRequestId));
        }

        if (!TryAcquireAttempt(proposalId, out var entry))
        {
            return LocalUnavailable();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actor = new ActorContext(entry.Context.ActorId, entry.CorrelationId);
            var durable = await CallDurableAsync(
                token => confirmApproval(
                    actor,
                    entry.Context.CompanyId,
                    entry.ApprovalId,
                    proposalId,
                    confirmationRequestId,
                    entry.Payload,
                    VinxToolPolicy.PolicyVersion,
                    token),
                cancellationToken).ConfigureAwait(false);
            if (!durable.IsSuccess)
            {
                return MapConfirmFailure(durable.ErrorCode!.Value);
            }

            var value = durable.Value;
            if (value is null || value.ApprovalId != entry.ApprovalId || value.ProposalId != proposalId)
            {
                throw new InvalidOperationException(
                    "Resultado durável divergente do registro local (fail-closed).");
            }

            var (owner, joinedOutcome) = RouteDurableOutcomeUnderLock(
                proposalId, entry, value.Outcome, confirmationRequestId);
            if (!owner && joinedOutcome is null)
            {
                throw new InvalidOperationException(
                    "Estado terminal local incompatível com o resultado durável (fail-closed).");
            }

            var outcome = owner
                ? value.Outcome == ConfirmApprovalOutcome.Replay
                    ? ConfirmApprovalOutcome.Replay
                    : ConfirmApprovalOutcome.Claimed
                : joinedOutcome!.Value;
            var published = await AwaitPublicationAsync(entry, outcome, cancellationToken).ConfigureAwait(false);
            if (published is not null)
            {
                return published;
            }

            var response = await AwaitExecutionCompletionAsync(entry, cancellationToken).ConfigureAwait(false);
            return new HarnessConfirmationResult(outcome, response, null);
        }
        finally
        {
            ReleaseAttempt(proposalId, entry);
        }
    }

    /// <summary>
    /// Veta a proposta: o veto durável decide; somente a primeira entrega
    /// (CAS) devolve o veto ao agente. Conflito/indisponibilidade não entregam
    /// nem removem estado.
    /// </summary>
    public async Task<HarnessVetoResult> VetoAsync(
        Guid proposalId,
        Guid vetoRequestId,
        CancellationToken cancellationToken = default)
    {
        if (proposalId == Guid.Empty)
        {
            throw new ArgumentException("O proposalId não pode ser vazio.", nameof(proposalId));
        }

        if (vetoRequestId == Guid.Empty)
        {
            throw new ArgumentException("O RequestId de veto não pode ser vazio.", nameof(vetoRequestId));
        }

        if (!TryAcquireAttempt(proposalId, out var entry))
        {
            return new HarnessVetoResult(
                VetoApprovalOutcome.ProposalUnavailable, null, AuthorizationErrorCode.ProposalUnavailable);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actor = new ActorContext(entry.Context.ActorId, entry.CorrelationId);
            var durable = await CallDurableAsync(
                token => vetoApproval(
                    actor,
                    entry.Context.CompanyId,
                    entry.ApprovalId,
                    proposalId,
                    vetoRequestId,
                    token),
                cancellationToken).ConfigureAwait(false);
            if (!durable.IsSuccess)
            {
                return MapVetoFailure(durable.ErrorCode!.Value);
            }

            var value = durable.Value;
            if (value is null || value.ApprovalId != entry.ApprovalId || value.ProposalId != proposalId)
            {
                throw new InvalidOperationException(
                    "Resultado durável divergente do registro local (fail-closed).");
            }

            if (value.Outcome is not (VetoApprovalOutcome.Vetoed or VetoApprovalOutcome.AlreadyVetoed))
            {
                throw new InvalidOperationException(
                    "Resultado durável de veto inválido como sucesso (fail-closed).");
            }

            Task<AgentResponse> deliveryTask;
            lock (gate)
            {
                if (!IsCurrentEntryLocked(proposalId, entry))
                {
                    throw new InvalidOperationException(
                        "Veto durável consumido sem registro local correspondente (fail-closed).");
                }

                switch (entry.Terminal)
                {
                    case TerminalOwner.None:
                        entry.Terminal = TerminalOwner.Vetoed;
                        entry.VetoDelivery = NewTcs<AgentResponse>();
                        entry.VetoDeliveryCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCts.Token);
                        entry.VetoDeadlineAt = timeProvider.GetUtcNow() + vetoDeliveryTimeout;
                        entry.VetoDeadlineTimer = timeProvider.CreateTimer(
                            _ =>
                            {
                                try
                                {
                                    entry.VetoDeliveryCts.Cancel();
                                }
                                catch (ObjectDisposedException)
                                {
                                }
                            },
                            null,
                            vetoDeliveryTimeout,
                            Timeout.InfiniteTimeSpan);
                        entry.VetoTask = DeliverVetoAsync(proposalId, entry, entry.VetoDeliveryCts.Token);
                        deliveryTask = entry.VetoDelivery.Task;
                        break;
                    case TerminalOwner.Vetoed:
                        deliveryTask = entry.VetoDelivery!.Task;
                        break;
                    default:
                        throw new InvalidOperationException(
                            "Estado terminal local incompatível com veto durável (fail-closed).");
                }
            }

            var remaining = entry.VetoDeadlineAt!.Value - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            var response = await deliveryTask.WaitAsync(remaining, timeProvider, cancellationToken)
                .ConfigureAwait(false);
            return new HarnessVetoResult(value.Outcome, response, null);
        }
        finally
        {
            ReleaseAttempt(proposalId, entry);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        lock (gate)
        {
            isStopping = true;
        }

        await sweepTimer.DisposeAsync().ConfigureAwait(false);
        shutdownCts.Cancel();

        List<PendingProposal> entries;
        List<Task> ownerSnapshots;
        lock (gate)
        {
            entries = proposals.Values.ToList();
            proposals.Clear();
            foreach (var entry in entries)
            {
                entry.DetachedForShutdown = true;
                if (entry.Terminal == TerminalOwner.None)
                {
                    entry.Terminal = TerminalOwner.Expired;
                }

                entry.ResumeSignal.TrySetResult(entry.Resumed);
                entry.Completion.TrySetCanceled(shutdownCts.Token);
                entry.VetoDelivery?.TrySetCanceled(shutdownCts.Token);
            }

            ownerSnapshots = entries
                .Select(entry => entry.AttemptsDrained.Task)
                .Concat(entries.Where(entry => entry.ExecutionTask is not null).Select(entry => entry.ExecutionTask!))
                .Concat(entries.Where(entry => entry.VetoTask is not null).Select(entry => entry.VetoTask!))
                .Concat(ownedCleanupTasks)
                .ToList();
        }

        var observedOwners = Task.WhenAll(ownerSnapshots.Select(ObserveAsync));
        var cleanupTasks = entries.Select(StartOwnedCleanup).ToList();
        var finalization = Task.WhenAll(
            [
                ObserveAsync(observedOwners),
                .. cleanupTasks.Select(ObserveAsync),
            ]);

        try
        {
            await finalization.WaitAsync(shutdownDrainTimeout, timeProvider).ConfigureAwait(false);
            DisposeShutdownCtsOnce();
        }
        catch (TimeoutException)
        {
            ScheduleDeferredShutdownCleanup(finalization);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Dreno de encerramento do coordenador falhou de forma segura.");
            ScheduleDeferredShutdownCleanup(finalization);
        }
    }

    private Task StartOwnedCleanup(PendingProposal entry)
    {
        var cleanupTask = CleanupDetachedEntryAsync(entry);
        lock (gate)
        {
            ownedCleanupTasks.RemoveWhere(task => task.IsCompleted);
            ownedCleanupTasks.Add(cleanupTask);
        }

        return cleanupTask;
    }

    private async Task CleanupDetachedEntryAsync(PendingProposal entry)
    {
        try
        {
            await DisposeExecutionResourcesOnceAsync(entry).ConfigureAwait(false);
            await DisposeVetoResourcesOnceAsync(entry).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Limpeza de proposta destacada falhou de forma segura.");
        }
    }

    private void ScheduleDeferredShutdownCleanup(Task finalization)
    {
        lock (gate)
        {
            deferredShutdownCleanup ??= DisposeShutdownAfterFinalizationAsync(finalization);
        }
    }

    private async Task DisposeShutdownAfterFinalizationAsync(Task finalization)
    {
        await ObserveAsync(finalization).ConfigureAwait(false);
        DisposeShutdownCtsOnce();
    }

    private static HarnessConfirmationResult LocalUnavailable() =>
        new(
            ConfirmApprovalOutcome.ProposalUnavailable,
            null,
            AuthorizationErrorCode.ProposalUnavailable);

    private static TimeSpan RequirePositive(TimeSpan value, string name) =>
        value <= TimeSpan.Zero ? throw new ArgumentException("A duração deve ser positiva.", name) : value;

    private static TaskCompletionSource<T> NewTcs<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static HarnessConfirmationResult MapConfirmFailure(AuthorizationErrorCode code) => code switch
    {
        AuthorizationErrorCode.CompanyContextRequired =>
            new(ConfirmApprovalOutcome.ProposalUnavailable, null, code),
        AuthorizationErrorCode.MembershipRequired =>
            new(ConfirmApprovalOutcome.ProposalUnavailable, null, code),
        AuthorizationErrorCode.CapabilityDenied =>
            new(ConfirmApprovalOutcome.ProposalUnavailable, null, code),
        AuthorizationErrorCode.ResourceNotFound =>
            new(ConfirmApprovalOutcome.ProposalUnavailable, null, code),
        AuthorizationErrorCode.IdempotencyConflict =>
            new(ConfirmApprovalOutcome.Conflict, null, code),
        AuthorizationErrorCode.IntegrityConflict =>
            new(ConfirmApprovalOutcome.Conflict, null, code),
        AuthorizationErrorCode.AuditUnavailable =>
            new(ConfirmApprovalOutcome.ProposalUnavailable, null, code),
        AuthorizationErrorCode.ProposalUnavailable =>
            new(ConfirmApprovalOutcome.ProposalUnavailable, null, code),
        AuthorizationErrorCode.ProposalConflict =>
            new(ConfirmApprovalOutcome.Conflict, null, code),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unsupported authorization error code."),
    };

    private static HarnessVetoResult MapVetoFailure(AuthorizationErrorCode code) => code switch
    {
        AuthorizationErrorCode.CompanyContextRequired =>
            new(VetoApprovalOutcome.ProposalUnavailable, null, code),
        AuthorizationErrorCode.MembershipRequired =>
            new(VetoApprovalOutcome.ProposalUnavailable, null, code),
        AuthorizationErrorCode.CapabilityDenied =>
            new(VetoApprovalOutcome.ProposalUnavailable, null, code),
        AuthorizationErrorCode.ResourceNotFound =>
            new(VetoApprovalOutcome.ProposalUnavailable, null, code),
        AuthorizationErrorCode.IdempotencyConflict =>
            new(VetoApprovalOutcome.Conflict, null, code),
        AuthorizationErrorCode.IntegrityConflict =>
            new(VetoApprovalOutcome.Conflict, null, code),
        AuthorizationErrorCode.AuditUnavailable =>
            new(VetoApprovalOutcome.ProposalUnavailable, null, code),
        AuthorizationErrorCode.ProposalUnavailable =>
            new(VetoApprovalOutcome.ProposalUnavailable, null, code),
        AuthorizationErrorCode.ProposalConflict =>
            new(VetoApprovalOutcome.Conflict, null, code),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unsupported authorization error code."),
    };

    private bool TryAcquireAttempt(Guid proposalId, out PendingProposal entry)
    {
        lock (gate)
        {
            if (isStopping || !proposals.TryGetValue(proposalId, out entry!))
            {
                entry = null!;
                return false;
            }

            if (entry.ActiveAttempts == 0)
            {
                entry.AttemptsDrained = NewTcs<bool>();
            }

            entry.ActiveAttempts++;
            return true;
        }
    }

    private bool IsCurrentEntryLocked(Guid proposalId, PendingProposal entry) =>
        proposals.TryGetValue(proposalId, out var current) && ReferenceEquals(current, entry);

    private void ReleaseAttempt(Guid proposalId, PendingProposal entry)
    {
        lock (gate)
        {
            if (!IsCurrentEntryLocked(proposalId, entry) && !entry.DetachedForShutdown)
            {
                throw new InvalidOperationException(
                    "Tentativa liberada para registro local substituído ou ausente (fail-closed).");
            }

            if (entry.ActiveAttempts <= 0)
            {
                throw new InvalidOperationException("Tentativa de proposta liberada mais de uma vez (fail-closed).");
            }

            entry.ActiveAttempts--;
            if (entry.ActiveAttempts == 0)
            {
                entry.AttemptsDrained.TrySetResult(true);
            }
        }

        Sweep();
    }

    private async Task<AuthorizationResult<T>> CallDurableAsync<T>(
        Func<CancellationToken, Task<AuthorizationResult<T>>> call,
        CancellationToken callerToken)
    {
        using var durableCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCts.Token);
        var deadlineTimer = timeProvider.CreateTimer(
            _ =>
            {
                try
                {
                    durableCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            null,
            durableCallTimeout,
            Timeout.InfiniteTimeSpan);
        try
        {
            return await call(durableCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            if (shutdownCts.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException(
                "A chamada durável excedeu o prazo do coordenador; repita com o mesmo RequestId.");
        }
        finally
        {
            await deadlineTimer.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Matriz de roteamento do resultado durável sob lock. Retorna
    /// (owner, joinedOutcome): owner=true quando esta chamada finaliza (normal
    /// ou recuperada) e executa; joinedOutcome é o desfecho do chamador que
    /// entra em uma finalização já publicada. Violações de invariante lançam.
    /// </summary>
    private (bool Owner, ConfirmApprovalOutcome? JoinedOutcome) RouteDurableOutcomeUnderLock(
        Guid proposalId,
        PendingProposal entry,
        ConfirmApprovalOutcome durableOutcome,
        Guid confirmationRequestId)
    {
        lock (gate)
        {
            if (!IsCurrentEntryLocked(proposalId, entry))
            {
                throw new InvalidOperationException(
                    "Resultado durável consumido sem registro local correspondente (fail-closed).");
            }

            switch (durableOutcome)
            {
                case ConfirmApprovalOutcome.Claimed:
                    switch (entry.Terminal)
                    {
                        case TerminalOwner.None:
                            entry.Terminal = TerminalOwner.Claimed;
                            entry.WinningConfirmationRequestId = confirmationRequestId;
                            entry.Resumed = true;
                            StartExecutionLocked(proposalId, entry);
                            return (true, null);
                        case TerminalOwner.Claimed when entry.WinningConfirmationRequestId == confirmationRequestId:
                            return (false, ConfirmApprovalOutcome.Claimed);
                        default:
                            throw new InvalidOperationException(
                                "Claim durável com estado local incompatível (fail-closed).");
                    }

                case ConfirmApprovalOutcome.Replay:
                    switch (entry.Terminal)
                    {
                        case TerminalOwner.None:
                            // Posse recuperada: o mesmo RequestId venceu o claim
                            // durável, mas a finalização local anterior não foi
                            // observada (timeout/perda de entrega do resultado).
                            entry.Terminal = TerminalOwner.Claimed;
                            entry.WinningConfirmationRequestId = confirmationRequestId;
                            entry.Resumed = true;
                            StartExecutionLocked(proposalId, entry);
                            return (true, null);
                        case TerminalOwner.Claimed when entry.WinningConfirmationRequestId == confirmationRequestId:
                            return (false, ConfirmApprovalOutcome.Replay);
                        default:
                            throw new InvalidOperationException(
                                "Replay durável com estado local incompatível (fail-closed).");
                    }

                default:
                    throw new InvalidOperationException(
                        "Resultado durável inválido como sucesso (fail-closed).");
            }
        }
    }

    private void StartExecutionLocked(Guid proposalId, PendingProposal entry)
    {
        entry.ExecutionCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCts.Token);
        entry.ExecutionDeadlineAt = timeProvider.GetUtcNow() + executionTimeout;
        entry.ExecutionDeadlineTimer = timeProvider.CreateTimer(
            _ =>
            {
                try
                {
                    entry.ExecutionCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            null,
            executionTimeout,
            Timeout.InfiniteTimeSpan);
        var permit = new HarnessApprovalDeliveryPermit(proposalId, entry.CallId);
        entry.ExecutionStart = NewTcs<bool>();
        entry.ExecutionTask = ExecuteAfterStartAsync(
            entry.ExecutionStart.Task,
            proposalId,
            entry,
            permit,
            entry.ExecutionCts.Token);
        entry.ResumeSignal.TrySetResult(true);
        entry.ExecutionStart.TrySetResult(true);
    }

    private async Task ExecuteAfterStartAsync(
        Task start,
        Guid proposalId,
        PendingProposal entry,
        HarnessApprovalDeliveryPermit permit,
        CancellationToken executionToken)
    {
        await start.ConfigureAwait(false);
        await ExecuteAsync(proposalId, entry, permit, executionToken).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(
        Guid proposalId,
        PendingProposal entry,
        HarnessApprovalDeliveryPermit permit,
        CancellationToken executionToken)
    {
        try
        {
            var response = await approvalGate.ApproveAsync(
                entry.Bundle.Agent,
                entry.Session,
                entry.Request,
                proposalId,
                permit,
                entry.Context,
                executionToken).ConfigureAwait(false);
            entry.Completion.TrySetResult(response);
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            entry.Completion.TrySetCanceled(executionToken);
        }
        catch (Exception exception)
        {
            entry.Completion.TrySetException(exception);
        }
    }

    private async Task DeliverVetoAsync(
        Guid proposalId,
        PendingProposal entry,
        CancellationToken vetoDeliveryToken)
    {
        try
        {
            var response = await approvalGate.DenyAsync(
                entry.Bundle.Agent,
                entry.Session,
                entry.Request,
                proposalId,
                entry.Context,
                vetoDeliveryToken).ConfigureAwait(false);
            entry.ResumeSignal.TrySetResult(false);
            entry.Completion.TrySetCanceled();
            entry.VetoDelivery!.TrySetResult(response);
        }
        catch (OperationCanceledException) when (vetoDeliveryToken.IsCancellationRequested)
        {
            entry.ResumeSignal.TrySetResult(false);
            entry.Completion.TrySetCanceled();
            entry.VetoDelivery!.TrySetCanceled(vetoDeliveryToken);
        }
        catch (Exception exception)
        {
            entry.ResumeSignal.TrySetResult(false);
            entry.Completion.TrySetCanceled();
            entry.VetoDelivery!.TrySetException(exception);
        }
    }

    /// <summary>
    /// Espera limitada pela publicação da finalização (tarefa armazenada antes
    /// do sinal). Retorna resultado pronto quando a publicação não aconteceu
    /// dentro do TTL local; caso contrário segue para a espera da execução.
    /// </summary>
    private async Task<HarnessConfirmationResult?> AwaitPublicationAsync(
        PendingProposal entry,
        ConfirmApprovalOutcome outcome,
        CancellationToken callerToken)
    {
        var remaining = entry.ExpiresAt - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero && !entry.ResumeSignal.Task.IsCompleted)
        {
            return new HarnessConfirmationResult(
                ConfirmApprovalOutcome.ProposalUnavailable,
                null,
                AuthorizationErrorCode.ProposalUnavailable);
        }

        var boundedRemaining = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        bool willExecute;
        try
        {
            willExecute = await entry.ResumeSignal.Task
                .WaitAsync(boundedRemaining, timeProvider, callerToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new HarnessConfirmationResult(
                ConfirmApprovalOutcome.ProposalUnavailable,
                null,
                AuthorizationErrorCode.ProposalUnavailable);
        }

        return willExecute
            ? null
            : new HarnessConfirmationResult(
                ConfirmApprovalOutcome.ProposalUnavailable,
                null,
                AuthorizationErrorCode.ProposalUnavailable);
    }

    private async Task<AgentResponse> AwaitExecutionCompletionAsync(
        PendingProposal entry,
        CancellationToken callerToken)
    {
        var completion = entry.Completion.Task;
        if (completion.IsCompleted)
        {
            return await completion.ConfigureAwait(false);
        }

        var remaining = entry.ExecutionDeadlineAt!.Value - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException("O prazo de execução do coordenador expirou.");
        }

        return await completion
            .WaitAsync(remaining, timeProvider, callerToken)
            .ConfigureAwait(false);
    }

    private void Sweep()
    {
        List<PendingProposal> detached;
        lock (gate)
        {
            if (isStopping)
            {
                return;
            }

            ownedCleanupTasks.RemoveWhere(task => task.IsCompleted);
            var now = timeProvider.GetUtcNow();
            detached = new List<PendingProposal>();
            var candidates = proposals
                .Where(pair => CanDetachLocked(pair.Value, now))
                .ToList();
            foreach (var (proposalId, entry) in candidates)
            {
                if (entry.Terminal == TerminalOwner.None)
                {
                    entry.Terminal = TerminalOwner.Expired;
                    entry.ResumeSignal.TrySetResult(false);
                    entry.Completion.TrySetCanceled();
                }

                if (proposals.TryGetValue(proposalId, out var current) && ReferenceEquals(current, entry))
                {
                    proposals.Remove(proposalId);
                    detached.Add(entry);
                }
            }
        }

        foreach (var entry in detached)
        {
            StartOwnedCleanup(entry);
        }
    }

    private static bool CanDetachLocked(PendingProposal entry, DateTimeOffset now) =>
        entry.ExpiresAt <= now &&
        entry.ActiveAttempts == 0 &&
        (!entry.Resumed || entry.Completion.Task.IsCompleted) &&
        (entry.VetoDelivery is null || entry.VetoDelivery.Task.IsCompleted) &&
        (entry.ExecutionTask is null || entry.ExecutionTask.IsCompleted) &&
        (entry.VetoTask is null || entry.VetoTask.IsCompleted);

    private static async ValueTask DisposeExecutionResourcesOnceAsync(PendingProposal entry)
    {
        if (Interlocked.Exchange(ref entry.ExecutionResourcesDisposed, 1) != 0)
        {
            return;
        }

        if (entry.ExecutionTask is not null)
        {
            await ObserveAsync(entry.ExecutionTask).ConfigureAwait(false);
        }

        if (entry.ExecutionDeadlineTimer is not null)
        {
            await entry.ExecutionDeadlineTimer.DisposeAsync().ConfigureAwait(false);
        }

        entry.ExecutionCts?.Dispose();
    }

    private static async ValueTask DisposeVetoResourcesOnceAsync(PendingProposal entry)
    {
        if (Interlocked.Exchange(ref entry.VetoResourcesDisposed, 1) != 0)
        {
            return;
        }

        if (entry.VetoTask is not null)
        {
            await ObserveAsync(entry.VetoTask).ConfigureAwait(false);
        }

        if (entry.VetoDeadlineTimer is not null)
        {
            await entry.VetoDeadlineTimer.DisposeAsync().ConfigureAwait(false);
        }

        entry.VetoDeliveryCts?.Dispose();
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Observação: falhas já estão refletidas nos TCS compartilhados.
        }
    }

    private void DisposeShutdownCtsOnce()
    {
        if (Interlocked.Exchange(ref shutdownCtsDisposed, 1) != 0)
        {
            return;
        }

        try
        {
            shutdownCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private enum TerminalOwner
    {
        None,
        Claimed,
        Vetoed,
        Expired,
    }

    private sealed class PendingProposal
    {
        public required Guid ProposalId { get; init; }
        public required Guid ApprovalId { get; init; }
        public required Guid CorrelationId { get; init; }
        public required VinxExecutionContext Context { get; init; }
        public required HarnessAgentBundle Bundle { get; init; }
        public required AgentSession Session { get; init; }
        public required ToolApprovalRequestContent Request { get; init; }
        public required string CallId { get; init; }
        public required JsonElement Payload { get; init; }
        public required Guid ExecutionRequestId { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }

        public TaskCompletionSource<bool> ResumeSignal { get; } = NewTcs<bool>();
        public TaskCompletionSource<AgentResponse> Completion { get; } = NewTcs<AgentResponse>();
        public TaskCompletionSource<AgentResponse>? VetoDelivery { get; set; }
        public TaskCompletionSource<bool> AttemptsDrained { get; set; } = CompletedTcs();

        public int ActiveAttempts { get; set; }
        public bool Resumed { get; set; }
        public bool DetachedForShutdown { get; set; }
        public TerminalOwner Terminal { get; set; }
        public Guid WinningConfirmationRequestId { get; set; }

        public CancellationTokenSource? ExecutionCts { get; set; }
        public ITimer? ExecutionDeadlineTimer { get; set; }
        public DateTimeOffset? ExecutionDeadlineAt { get; set; }
        public TaskCompletionSource<bool>? ExecutionStart { get; set; }
        public Task? ExecutionTask { get; set; }
        public int ExecutionResourcesDisposed;

        public CancellationTokenSource? VetoDeliveryCts { get; set; }
        public ITimer? VetoDeadlineTimer { get; set; }
        public DateTimeOffset? VetoDeadlineAt { get; set; }
        public Task? VetoTask { get; set; }
        public int VetoResourcesDisposed;

        private static TaskCompletionSource<bool> CompletedTcs()
        {
            var tcs = NewTcs<bool>();
            tcs.SetResult(true);
            return tcs;
        }
    }
}
