using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.Foundation.Approvals;

namespace Vinx.Erp.AI;

/// <summary>
/// Delegado que executa a confirmação durável na Foundation. O coordenador é
/// agnóstico de banco de dados: em produção aponta para
/// <c>ConfirmApprovalService</c>; em testes recebe um duplo determinístico.
/// </summary>
public delegate Task<ConfirmApprovalOutcome> ConfirmApprovalDelegate(
    Guid companyId,
    Guid approvalId,
    Guid proposalId,
    Guid confirmationRequestId,
    JsonElement payload,
    string policyVersion,
    CancellationToken cancellationToken);

/// <summary>
/// Resultado da confirmação no coordenador. <see cref="Response"/> só existe
/// para <see cref="ConfirmApprovalOutcome.Claimed"/> (execução do vencedor) e
/// <see cref="ConfirmApprovalOutcome.Replay"/> (resultado reutilizado do
/// vencedor ainda em voo); conflitos e indisponibilidade nunca executam.
/// </summary>
public sealed record HarnessConfirmationResult(ConfirmApprovalOutcome Outcome, AgentResponse? Response);

/// <summary>
/// Coordenador de confirmações do harness (código do servidor). Possui o
/// agente, a sessão e a solicitação pendente por <c>proposalId</c> em um
/// registro em memória, e garante single-flight: somente a confirmação
/// vencedora envia <see cref="ToolApprovalRequestContent.CreateResponse(bool, string?)"/>
/// com aprovação e retoma a sessão; replays com o mesmo <c>RequestId</c> de
/// confirmação aguardam e reutilizam o resultado do vencedor; a mesma sessão
/// nunca é retomada duas vezes e a resposta de aprovação é sempre a interação
/// imediatamente seguinte. Estado em processo é removido em consumo,
/// expiração, conflito e veto; após reinício do processo uma proposta viva é
/// perdida e a confirmação devolve <see cref="ConfirmApprovalOutcome.ProposalUnavailable"/>
/// (sem promessa de replay de <see cref="AgentResponse"/>).
/// </summary>
public sealed class HarnessConfirmationCoordinator
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, PendingProposal> proposals = new();
    private readonly ConfirmApprovalDelegate confirmApproval;
    private readonly HarnessApprovalGate approvalGate;
    private readonly ILogger<HarnessConfirmationCoordinator> logger;

    public HarnessConfirmationCoordinator(
        ConfirmApprovalDelegate confirmApproval,
        HarnessApprovalGate approvalGate,
        ILogger<HarnessConfirmationCoordinator>? logger = null)
    {
        this.confirmApproval = confirmApproval ?? throw new ArgumentNullException(nameof(confirmApproval));
        this.approvalGate = approvalGate ?? throw new ArgumentNullException(nameof(approvalGate));
        this.logger = logger ?? NullLogger<HarnessConfirmationCoordinator>.Instance;
    }

    /// <summary>Propostas vivas mantidas em processo (somente para diagnóstico/testes).</summary>
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
    /// pendente. O <c>proposalId</c> opaco vem da preparação durável; o
    /// <c>RequestId</c> de execução é estável entre reinicializações e
    /// pertence ao serviço de domínio (escopo EXECUÇÃO). Registro duplicado,
    /// identificadores vazios ou expiração não futura falham fechados.
    /// </summary>
    public void Register(
        Guid proposalId,
        Guid approvalId,
        VinxExecutionContext context,
        AIAgent agent,
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

        if (executionRequestId == Guid.Empty)
        {
            throw new ArgumentException("O RequestId de execução não pode ser vazio.", nameof(executionRequestId));
        }

        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(agent);
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

        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("A expiração da proposta deve ser futura.", nameof(expiresAt));
        }

        var entry = new PendingProposal
        {
            ApprovalId = approvalId,
            Context = context,
            Agent = agent,
            Session = session,
            Request = pendingRequest,
            Payload = payload,
            ExecutionRequestId = executionRequestId,
            ExpiresAt = expiresAt,
        };

        lock (gate)
        {
            if (!proposals.TryAdd(proposalId, entry))
            {
                throw new InvalidOperationException(
                    $"Já existe proposta viva registrada para o proposalId '{proposalId}'.");
            }
        }
    }

    /// <summary>
    /// Confirma a proposta e retoma a sessão exatamente uma vez. A confirmação
    /// durável (escopo CONFIRMAÇÃO) decide o resultado; somente o vencedor
    /// (<see cref="ConfirmApprovalOutcome.Claimed"/>) envia a resposta de
    /// aprovação e retoma a sessão. O registro em processo é removido em
    /// consumo, expiração, conflito e indisponibilidade.
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

        PendingProposal? entry;
        lock (gate)
        {
            if (!proposals.TryGetValue(proposalId, out entry))
            {
                // Proposta sem correspondência em processo: reinício, veto ou
                // consumo já finalizado. Nada é executado.
                return new HarnessConfirmationResult(ConfirmApprovalOutcome.ProposalUnavailable, null);
            }

            if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                proposals.Remove(proposalId);
                return new HarnessConfirmationResult(ConfirmApprovalOutcome.ProposalUnavailable, null);
            }
        }

        var outcome = await confirmApproval(
            entry.Context.CompanyId,
            entry.ApprovalId,
            proposalId,
            confirmationRequestId,
            entry.Payload,
            VinxToolPolicy.PolicyVersion,
            cancellationToken).ConfigureAwait(false);

        switch (outcome)
        {
            case ConfirmApprovalOutcome.Claimed:
                return await ExecuteAsWinnerAsync(proposalId, entry, cancellationToken).ConfigureAwait(false);

            case ConfirmApprovalOutcome.Replay:
                var replayed = await AwaitWinnerCompletionAsync(proposalId, cancellationToken).ConfigureAwait(false);
                return replayed is null
                    ? new HarnessConfirmationResult(ConfirmApprovalOutcome.ProposalUnavailable, null)
                    : new HarnessConfirmationResult(ConfirmApprovalOutcome.Replay, replayed);

            case ConfirmApprovalOutcome.Conflict:
                Remove(proposalId);
                return new HarnessConfirmationResult(ConfirmApprovalOutcome.Conflict, null);

            default:
                Remove(proposalId);
                return new HarnessConfirmationResult(ConfirmApprovalOutcome.ProposalUnavailable, null);
        }
    }

    /// <summary>
    /// Veta a proposta: devolve o veto ao agente na mesma sessão e remove o
    /// estado em processo. Uma proposta vetada nunca mais executa.
    /// </summary>
    public async Task<AgentResponse> VetoAsync(Guid proposalId, CancellationToken cancellationToken = default)
    {
        PendingProposal? entry;
        lock (gate)
        {
            if (!proposals.TryGetValue(proposalId, out entry))
            {
                throw new InvalidOperationException(
                    $"Nenhuma proposta viva registrada para o proposalId '{proposalId}'.");
            }
        }

        var response = await approvalGate
            .DenyAsync(entry.Agent, entry.Session, entry.Request, entry.Context, cancellationToken)
            .ConfigureAwait(false);
        Remove(proposalId);
        return response;
    }

    private async Task<HarnessConfirmationResult> ExecuteAsWinnerAsync(
        Guid proposalId,
        PendingProposal entry,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<AgentResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AgentResponse>? alreadyResumed = null;

        lock (gate)
        {
            if (!proposals.TryGetValue(proposalId, out var current) || !ReferenceEquals(current, entry))
            {
                // A proposta desapareceu entre a reivindicação e a retomada
                // (veto/expiração concorrente). Fail-closed: nada executa.
                return new HarnessConfirmationResult(ConfirmApprovalOutcome.ProposalUnavailable, null);
            }

            if (entry.Resumed)
            {
                // Defesa em profundidade: a mesma sessão nunca é retomada duas
                // vezes. Quem perdeu a corrida aguarda o vencedor.
                alreadyResumed = entry.Completion!.Task;
            }
            else
            {
                entry.Resumed = true;
                entry.Completion = completion;
            }
        }

        if (alreadyResumed is not null)
        {
            return new HarnessConfirmationResult(ConfirmApprovalOutcome.Replay, await alreadyResumed.ConfigureAwait(false));
        }

        try
        {
            // A resposta de aprovação é a interação imediatamente seguinte com
            // a sessão; somente o vencedor chega aqui.
            var approvalMessage = new ChatMessage(ChatRole.User, [entry.Request.CreateResponse(true)]);
            HarnessApprovalGate.ThrowIfUnmatchedApprovalResponse(approvalMessage, entry.Request);
            var response = await entry.Agent
                .RunAsync(approvalMessage, entry.Session, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            completion.SetResult(response);
            logger.LogInformation(
                "Proposta {ProposalId} executada pelo vencedor no módulo AI.", proposalId);
            return new HarnessConfirmationResult(ConfirmApprovalOutcome.Claimed, response);
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
            logger.LogWarning(
                exception,
                "Falha na execução da proposta {ProposalId} no módulo AI; estado em processo removido.",
                proposalId);
            throw;
        }
        finally
        {
            // Consumo (sucesso ou falha) remove o estado em processo; replays
            // posteriores à remoção recebem ProposalUnavailable.
            Remove(proposalId);
        }
    }

    private async Task<AgentResponse?> AwaitWinnerCompletionAsync(
        Guid proposalId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task<AgentResponse>? completion;
            lock (gate)
            {
                if (!proposals.TryGetValue(proposalId, out var entry))
                {
                    // O vencedor já finalizou e removeu o estado: o replay do
                    // resultado em processo não é prometido.
                    return null;
                }

                completion = entry.Completion?.Task;
            }

            if (completion is not null)
            {
                return await completion.ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    private void Remove(Guid proposalId)
    {
        lock (gate)
        {
            proposals.Remove(proposalId);
        }
    }

    private sealed class PendingProposal
    {
        public required Guid ApprovalId { get; init; }

        public required VinxExecutionContext Context { get; init; }

        public required AIAgent Agent { get; init; }

        public required AgentSession Session { get; init; }

        public required ToolApprovalRequestContent Request { get; init; }

        public required JsonElement Payload { get; init; }

        public required Guid ExecutionRequestId { get; init; }

        public required DateTimeOffset ExpiresAt { get; init; }

        public bool Resumed { get; set; }

        public TaskCompletionSource<AgentResponse>? Completion { get; set; }
    }
}
