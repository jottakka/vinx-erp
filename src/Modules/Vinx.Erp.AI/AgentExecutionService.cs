using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.AI;

/// <summary>
/// Executa um comando do agente com ledger monotônico. O estado é gravado antes
/// de toda fronteira externa; depois dela, qualquer resultado não conhecido é
/// ambíguo e nunca volta a disparar provider automaticamente.
/// </summary>
public sealed class AgentExecutionService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    AgentRunner runner)
{
    private const string CommandName = "agent.run";
    private const string ResourceType = "agent_run";
    private const string StaleInFlightReason = "execution_stale";
    private const string CancelledReason = "provider_cancelled";
    private const string UnavailableReason = "provider_unavailable";
    private static readonly TimeSpan InFlightStaleAfter = TimeSpan.FromMinutes(10);

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Não há chave company-scoped sem empresa. O runner ainda resolve a
        // resposta determinística, mas não inventamos um registro global.
        if (command.CompanyId is null)
        {
            var outcome = await RunAsync(command, lifecycle: null, cancellationToken).ConfigureAwait(false);
            if (outcome.Status is not (AgentOutcomeStatus.Unavailable or AgentOutcomeStatus.TimedOut))
            {
                await AppendGlobalAuditAsync(command, outcome, cancellationToken).ConfigureAwait(false);
            }

            return AgentExecutionResult.Completed(outcome);
        }

        var payloadHash = ComputePayloadHash(command);
        var claim = await ClaimOrReplayAsync(command, payloadHash, cancellationToken).ConfigureAwait(false);
        if (claim.Result is not null)
        {
            return claim.Result;
        }

        var lifecycle = new AgentExecutionLifecycle(database, claim.Ledger!, command);
        try
        {
            var outcome = await RunAsync(command, lifecycle, cancellationToken).ConfigureAwait(false);
            if (lifecycle.ProviderDispatched && outcome.Status is AgentOutcomeStatus.TimedOut or AgentOutcomeStatus.Unavailable)
            {
                return await MarkAmbiguousAsync(
                    claim.Ledger!, command, outcome.ReasonCode, CancellationToken.None, outcome).ConfigureAwait(false);
            }

            if (lifecycle.ProviderDispatched || outcome.Status is AgentOutcomeStatus.Completed
                or AgentOutcomeStatus.NeedsClarification or AgentOutcomeStatus.Denied
                or AgentOutcomeStatus.Invalid or AgentOutcomeStatus.RequiresConfirmation)
            {
                var safeOutcome = ToSafePersistedOutcome(outcome);
                claim.Ledger!.Complete(JsonSerializer.Serialize(safeOutcome), command.Actor.UserId, command.RequestId);
                await PersistTerminalAsync(claim.Ledger, command, safeOutcome, CancellationToken.None).ConfigureAwait(false);
                return AgentExecutionResult.Completed(safeOutcome);
            }

            claim.Ledger!.FailClosed(outcome.ReasonCode, command.Actor.UserId, command.RequestId);
            await PersistTerminalAsync(claim.Ledger, command, outcome: null, CancellationToken.None).ConfigureAwait(false);
            return AgentExecutionResult.FailedClosed(outcome.ReasonCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (lifecycle.ProviderDispatched)
            {
                await MarkAmbiguousAsync(claim.Ledger!, command, CancelledReason, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await FailClosedAsync(claim.Ledger!, command, CancelledReason, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            return lifecycle.ProviderDispatched
                ? await MarkAmbiguousAsync(claim.Ledger!, command, CancelledReason, CancellationToken.None).ConfigureAwait(false)
                : await FailClosedAsync(claim.Ledger!, command, CancelledReason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return lifecycle.ProviderDispatched
                ? await MarkAmbiguousAsync(claim.Ledger!, command, UnavailableReason, CancellationToken.None).ConfigureAwait(false)
                : await FailClosedAsync(claim.Ledger!, command, UnavailableReason, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<AgentOutcome> RunAsync(
        AgentCommand command,
        IAgentExecutionLifecycle? lifecycle,
        CancellationToken cancellationToken) =>
        await runner.RunAsync(
            command.Actor,
            command.Journey,
            command.Message,
            command.CompanyId,
            command.Attachments,
            lifecycle,
            cancellationToken).ConfigureAwait(false);

    private async Task<(AgentExecutionLedger? Ledger, AgentExecutionResult? Result)> ClaimOrReplayAsync(
        AgentCommand command,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        var companyId = command.CompanyId!.Value;
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyContextAsync(command, companyId, cancellationToken).ConfigureAwait(false);
            var existing = await FindAsync(command, companyId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return (existing, await ReplayAsync(existing, command, payloadHash, cancellationToken).ConfigureAwait(false));
            }

            var ledger = AgentExecutionLedger.Create(
                Guid.NewGuid(), companyId, command.Actor.UserId, command.Channel, command.InboundMessageId,
                command.RequestId, command.Journey, payloadHash);
            database.AgentExecutionLedger.Add(ledger);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (ledger, null);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            database.ChangeTracker.Clear();
            await using var replayTransaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ApplyContextAsync(command, companyId, cancellationToken).ConfigureAwait(false);
            var existing = await FindAsync(command, companyId, cancellationToken).ConfigureAwait(false);
            await replayTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing is null
                ? (null, AgentExecutionResult.FailedClosed(UnavailableReason))
                : (existing, await ReplayAsync(existing, command, payloadHash, cancellationToken).ConfigureAwait(false));
        }
    }

    private async Task<AgentExecutionResult> ReplayAsync(
        AgentExecutionLedger ledger,
        AgentCommand command,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(ledger.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            await AppendConflictAuditAsync(command, ledger.CompanyId, cancellationToken).ConfigureAwait(false);
            return AgentExecutionResult.Conflict();
        }

        return ledger.State switch
        {
            AgentExecutionLedgerState.Completed => DeserializeCompleted(ledger),
            AgentExecutionLedgerState.Ambiguous => AgentExecutionResult.Ambiguous(ledger.ReasonCode!),
            AgentExecutionLedgerState.FailedClosed => AgentExecutionResult.FailedClosed(ledger.ReasonCode!),
            AgentExecutionLedgerState.Claimed
                when DateTimeOffset.UtcNow - ledger.UpdatedAt >= InFlightStaleAfter =>
                await FailClosedAsync(ledger, command, StaleInFlightReason, cancellationToken).ConfigureAwait(false),
            AgentExecutionLedgerState.VisionInFlight or AgentExecutionLedgerState.ModelInFlight
                when DateTimeOffset.UtcNow - ledger.UpdatedAt >= InFlightStaleAfter =>
                await MarkAmbiguousAsync(ledger, command, StaleInFlightReason, cancellationToken).ConfigureAwait(false),
            _ => AgentExecutionResult.InProgress(),
        };
    }

    private async Task<AgentExecutionResult> MarkAmbiguousAsync(
        AgentExecutionLedger ledger,
        AgentCommand command,
        string reasonCode,
        CancellationToken cancellationToken,
        AgentOutcome? observedOutcome = null)
    {
        if (ledger.State is AgentExecutionLedgerState.VisionInFlight or AgentExecutionLedgerState.ModelInFlight)
        {
            ledger.MarkAmbiguous(reasonCode, command.Actor.UserId, command.RequestId);
            await PersistTerminalAsync(ledger, command, outcome: null, cancellationToken).ConfigureAwait(false);
        }

        return AgentExecutionResult.Ambiguous(reasonCode, observedOutcome);
    }

    private async Task<AgentExecutionResult> FailClosedAsync(
        AgentExecutionLedger ledger,
        AgentCommand command,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        if (ledger.State == AgentExecutionLedgerState.Claimed)
        {
            ledger.FailClosed(reasonCode, command.Actor.UserId, command.RequestId);
            await PersistTerminalAsync(ledger, command, outcome: null, cancellationToken).ConfigureAwait(false);
        }

        return AgentExecutionResult.FailedClosed(reasonCode);
    }

    private async Task PersistTerminalAsync(
        AgentExecutionLedger ledger,
        AgentCommand command,
        AgentOutcome? outcome,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ApplyContextAsync(command, ledger.CompanyId, cancellationToken).ConfigureAwait(false);
        var (status, reasonCode, auditOutcome) = outcome is null
            ? (ledger.State == AgentExecutionLedgerState.Ambiguous ? "ambiguous" : "failed_closed", ledger.ReasonCode, AuditOutcome.Failure)
            : ToAudit(outcome);
        await auditAppender.AppendAsync(new AuditEventData(
            command.Actor.UserId,
            AuditScope.Company,
            ledger.CompanyId,
            AuditSource.Ai,
            CommandName,
            ResourceType,
            command.RequestId,
            auditOutcome,
            command.Actor.CorrelationId,
            reasonCode,
            JsonSerializer.Serialize(new AgentExecutionAuditMetadata(status, reasonCode))), cancellationToken).ConfigureAwait(false);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendConflictAuditAsync(
        AgentCommand command,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ApplyContextAsync(command, companyId, cancellationToken).ConfigureAwait(false);
        await auditAppender.AppendAsync(new AuditEventData(
            command.Actor.UserId,
            AuditScope.Company,
            companyId,
            AuditSource.Ai,
            CommandName,
            ResourceType,
            command.RequestId,
            AuditOutcome.Denied,
            command.Actor.CorrelationId,
            "idempotency_conflict",
            JsonSerializer.Serialize(new AgentExecutionAuditMetadata("conflict", "idempotency_conflict"))), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendGlobalAuditAsync(
        AgentCommand command,
        AgentOutcome outcome,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (status, reasonCode, auditOutcome) = ToAudit(outcome);
        await auditAppender.AppendAsync(new AuditEventData(
            command.Actor.UserId,
            AuditScope.Global,
            requestedCompanyId: null,
            AuditSource.Ai,
            CommandName,
            ResourceType,
            command.RequestId,
            auditOutcome,
            command.Actor.CorrelationId,
            reasonCode,
            JsonSerializer.Serialize(new AgentExecutionAuditMetadata(status, reasonCode))), cancellationToken).ConfigureAwait(false);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentExecutionLedger?> FindAsync(AgentCommand command, Guid companyId, CancellationToken cancellationToken) =>
        await database.AgentExecutionLedger.SingleOrDefaultAsync(ledger =>
            ledger.CompanyId == companyId
            && ledger.ActorUserId == command.Actor.UserId
            && ledger.Channel == command.Channel
            && ledger.RequestId == command.RequestId,
            cancellationToken).ConfigureAwait(false);

    private Task ApplyContextAsync(AgentCommand command, Guid companyId, CancellationToken cancellationToken) =>
        FoundationRlsContext.ApplyCommandAsync(database.Database, command.Actor, companyId, cancellationToken);

    private static AgentExecutionResult DeserializeCompleted(AgentExecutionLedger ledger) =>
        JsonSerializer.Deserialize<AgentOutcome>(ledger.SafeResultJson!) is { } outcome
            ? AgentExecutionResult.Completed(outcome)
            : AgentExecutionResult.FailedClosed("safe_result_invalid");

    private static AgentOutcome ToSafePersistedOutcome(AgentOutcome outcome) =>
        outcome.Status == AgentOutcomeStatus.RequiresConfirmation
            ? outcome with { ProposedActionName = null, PaguaiProposal = null }
            : outcome;

    private static (string Status, string ReasonCode, AuditOutcome Outcome) ToAudit(AgentOutcome outcome) => outcome.Status switch
    {
        AgentOutcomeStatus.Completed => ("result", outcome.ReasonCode, AuditOutcome.Success),
        AgentOutcomeStatus.NeedsClarification => ("needs_clarification", outcome.ReasonCode, AuditOutcome.Success),
        AgentOutcomeStatus.RequiresConfirmation => ("requires_confirmation", outcome.ReasonCode, AuditOutcome.Success),
        AgentOutcomeStatus.Denied => ("denied", outcome.ReasonCode, AuditOutcome.Denied),
        AgentOutcomeStatus.Invalid => ("validation_error", outcome.ReasonCode, AuditOutcome.Denied),
        _ => ("failed_closed", outcome.ReasonCode, AuditOutcome.Failure),
    };

    private static string ComputePayloadHash(AgentCommand command) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            command.Journey,
            command.Message,
            command.CompanyId,
            command.Attachments,
        })))).ToLowerInvariant();

    private sealed record AgentExecutionAuditMetadata(string Status, string? ReasonCode);

    private sealed class AgentExecutionLifecycle(
        FoundationDbContext database,
        AgentExecutionLedger ledger,
        AgentCommand command) : IAgentExecutionLifecycle
    {
        public bool ProviderDispatched { get; private set; }

        public async Task MarkVisionInFlightAsync(CancellationToken cancellationToken)
        {
            if (ProviderDispatched)
            {
                return;
            }

            ledger.MarkVisionInFlight(command.Actor.UserId, command.RequestId);
            await PersistTransitionAsync(cancellationToken).ConfigureAwait(false);
            ProviderDispatched = true;
        }

        public async Task MarkModelInFlightAsync(CancellationToken cancellationToken)
        {
            if (ledger.State != AgentExecutionLedgerState.ModelInFlight)
            {
                ledger.MarkModelInFlight(command.Actor.UserId, command.RequestId);
                await PersistTransitionAsync(cancellationToken).ConfigureAwait(false);
            }

            ProviderDispatched = true;
        }

        private async Task PersistTransitionAsync(CancellationToken cancellationToken)
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await FoundationRlsContext.ApplyCommandAsync(
                database.Database,
                command.Actor,
                ledger.CompanyId,
                cancellationToken).ConfigureAwait(false);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
