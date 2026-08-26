using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Approvals;

/// <summary>
/// Confirmação ou veto durável de aprovações do módulo AI (fase 2 do fluxo de
/// seis fases). O claim é single-use: um único UPDATE condicional decide o
/// vencedor usando o relógio do banco (<c>now()</c>); replays do mesmo
/// <c>RequestId</c> (escopo CONFIRMATION) reutilizam o resultado original e
/// qualquer outra confirmação recebe conflito ou indisponibilidade com erro
/// redacted e negativa auditada quando aplicável.
/// </summary>
public sealed class ConfirmApprovalService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    AuthorizationTelemetry telemetry,
    CapabilityEvaluator capabilityEvaluator,
    ILogger<ConfirmApprovalService> logger,
    ApprovalCapabilityPolicy? approvalPolicy = null)
{
    private const string Operation = "approval_confirmation";
    private const string Module = "foundation";

    // Costura aditiva: Legacy preserva o contrato do módulo AI byte a byte.
    private readonly ApprovalCapabilityPolicy policy = approvalPolicy ?? ApprovalCapabilityPolicy.Legacy;

    private CapabilityRequirement RequiredCapability =>
        new(policy.CapabilityName, CapabilityScope.Company);

    private string ConfirmCommandName => policy.ConfirmCommandName;

    private string VetoCommandName => policy.VetoCommandName;

    public async Task<AuthorizationResult<ConfirmApprovalResult>> ConfirmAsync(
        ActorContext actor,
        ConfirmApprovalCommand command,
        CancellationToken cancellationToken)
    {
        var payloadHash = CanonicalPayloadHasher.ComputeHash(command.Payload);
        AuthorizationErrorCode? denial = null;

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            var actorMembership = await database.Memberships.SingleOrDefaultAsync(membership =>
                membership.CompanyId == command.CompanyId && membership.UserId == actor.UserId,
                cancellationToken);
            if (actorMembership is null)
            {
                denial = AuthorizationErrorCode.MembershipRequired;
            }
            else if (!await capabilityEvaluator.HasGrantAsync(
                         command.CompanyId,
                         actorMembership.Id,
                         RequiredCapability,
                         cancellationToken))
            {
                denial = AuthorizationErrorCode.CapabilityDenied;
            }
            else
            {
                var approval = await database.Approvals.SingleOrDefaultAsync(candidate =>
                    candidate.CompanyId == command.CompanyId && candidate.Id == command.ApprovalId,
                    cancellationToken);
                if (approval is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return ProposalUnavailable("proposal_not_found");
                }

                if (approval.ProposalId != command.ProposalId)
                {
                    denial = AuthorizationErrorCode.ProposalConflict;
                }
                else if (approval.IsConsumed)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    if (MatchesReplay(approval, command, payloadHash))
                    {
#pragma warning disable CA1873 // Os argumentos são constantes ou valores baratos; o guard de logging não agrega ganho.
                        logger.LogInformation(
                            "Confirmação de aprovação {Operation} {Module} {Result} {Reason}",
                            Operation,
                            Module,
                            "replayed",
                            "same_confirmation_request_replayed");
#pragma warning restore CA1873
                        return AuthorizationResult<ConfirmApprovalResult>.Success(
                            new ConfirmApprovalResult(approval.Id, approval.ProposalId, ConfirmApprovalOutcome.Replay));
                    }

                    return await ReturnAuditedDenialAsync(
                        actor,
                        command.CompanyId,
                        AuthorizationErrorCode.ProposalConflict,
                        "proposal_already_consumed",
                        cancellationToken);
                }
                else if (!string.Equals(approval.PayloadHash, payloadHash, StringComparison.Ordinal)
                         || !string.Equals(approval.PolicyVersion, command.PolicyVersion, StringComparison.Ordinal))
                {
                    denial = AuthorizationErrorCode.ProposalConflict;
                }
                else
                {
                    var result = new ConfirmApprovalResult(approval.Id, approval.ProposalId, ConfirmApprovalOutcome.Claimed);
                    var resultJson = JsonSerializer.Serialize(result);
                    var claimedRows = await database.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                         UPDATE foundation.approvals
                         SET consumed_at = now(),
                             confirmation_request_id = {command.RequestId},
                             result_json = {resultJson}::jsonb,
                             updated_by_user_id = {actor.UserId},
                             updated_at = now(),
                             row_version = row_version + 1
                         WHERE id = {command.ApprovalId}
                           AND company_id = {command.CompanyId}
                           AND proposal_id = {command.ProposalId}
                           AND payload_hash = {payloadHash}
                           AND policy_version = {command.PolicyVersion}
                           AND consumed_at IS NULL
                           AND deleted_at IS NULL
                           AND expires_at > now()
                         """,
                        cancellationToken);
                    if (claimedRows == 1)
                    {
                        await auditAppender.AppendAsync(new AuditEventData(
                            actor.UserId,
                            AuditScope.Company,
                            command.CompanyId,
                            policy.AuditSource,
                            ConfirmCommandName,
                            "approval",
                            approval.Id,
                            AuditOutcome.Success,
                            new CorrelationContext(actor.CorrelationId).CorrelationId,
                            null,
                            "{}"), cancellationToken);
                        await database.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
#pragma warning disable CA1873 // Os argumentos são constantes ou valores baratos; o guard de logging não agrega ganho.
                        logger.LogInformation(
                            "Confirmação de aprovação {Operation} {Module} {Result} {Reason}",
                            Operation,
                            Module,
                            "claimed",
                            "single_use_approval_claimed");
#pragma warning restore CA1873
                        return AuthorizationResult<ConfirmApprovalResult>.Success(result);
                    }

                    // Zero linhas: outra confirmação venceu, a proposta expirou
                    // ou foi vetada entre a leitura e o claim. Reconciliação
                    // dentro da mesma transação (contexto RLS ainda ativo).
                    var current = await database.Approvals.AsNoTracking().IgnoreQueryFilters()
                        .SingleOrDefaultAsync(candidate =>
                            candidate.CompanyId == command.CompanyId && candidate.Id == command.ApprovalId,
                            cancellationToken);
                    await transaction.RollbackAsync(cancellationToken);
                    if (current is null)
                    {
                        return ProposalUnavailable("proposal_expired_or_vetoed");
                    }

                    if (MatchesReplay(current, command, payloadHash))
                    {
                        return AuthorizationResult<ConfirmApprovalResult>.Success(
                            new ConfirmApprovalResult(current.Id, current.ProposalId, ConfirmApprovalOutcome.Replay));
                    }

                    if (current.IsConsumed)
                    {
                        return await ReturnAuditedDenialAsync(
                            actor,
                            command.CompanyId,
                            AuthorizationErrorCode.ProposalConflict,
                            "proposal_claim_lost",
                            cancellationToken);
                    }

                    return ProposalUnavailable("proposal_expired_or_vetoed");
                }
            }

            await transaction.RollbackAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(
                exception,
                "Confirmação de aprovação {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "retrying",
                "concurrent_write");
            database.ChangeTracker.Clear();
            return await ReconcileConcurrentClaimAsync(actor, command, payloadHash, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            return AuditUnavailable(AuditUnavailablePhase.SuccessTransaction, exception);
        }

        if (denial == AuthorizationErrorCode.ProposalConflict)
        {
            return await ReturnAuditedDenialAsync(
                actor,
                command.CompanyId,
                AuthorizationErrorCode.ProposalConflict,
                "proposal_divergence",
                cancellationToken);
        }

        return await ReturnAuditedDenialAsync(actor, command.CompanyId, denial!.Value, "authorization_denied", cancellationToken);
    }

    public async Task<AuthorizationResult<VetoApprovalResult>> VetoAsync(
        ActorContext actor,
        VetoApprovalCommand command,
        CancellationToken cancellationToken)
    {
        AuthorizationErrorCode? denial = null;

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            var actorMembership = await database.Memberships.SingleOrDefaultAsync(membership =>
                membership.CompanyId == command.CompanyId && membership.UserId == actor.UserId,
                cancellationToken);
            if (actorMembership is null)
            {
                denial = AuthorizationErrorCode.MembershipRequired;
            }
            else if (!await capabilityEvaluator.HasGrantAsync(
                          command.CompanyId,
                          actorMembership.Id,
                          RequiredCapability,
                          cancellationToken))
            {
                denial = AuthorizationErrorCode.CapabilityDenied;
            }
            else
            {
                // Claim condicional único pelo relógio do banco: vetar é
                // soft-delete terminal com motivo fixo, vínculo de proposta e
                // RequestId do veto, na mesma transação da auditoria.
                var vetoedRows = await database.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE foundation.approvals
                     SET deleted_at = now(),
                         deleted_by_user_id = {actor.UserId},
                         deleted_reason = 'approval_vetoed',
                         updated_by_user_id = {actor.UserId},
                         updated_at = now(),
                         request_id = {command.RequestId},
                         row_version = row_version + 1
                     WHERE id = {command.ApprovalId}
                       AND company_id = {command.CompanyId}
                       AND proposal_id = {command.ProposalId}
                       AND consumed_at IS NULL
                       AND deleted_at IS NULL
                       AND expires_at > now()
                     """,
                    cancellationToken);
                if (vetoedRows == 1)
                {
                    await auditAppender.AppendAsync(new AuditEventData(
                        actor.UserId,
                        AuditScope.Company,
                        command.CompanyId,
                        policy.AuditSource,
                        VetoCommandName,
                        "approval",
                        command.ApprovalId,
                        AuditOutcome.Success,
                        new CorrelationContext(actor.CorrelationId).CorrelationId,
                        null,
                        "{}"), cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
#pragma warning disable CA1873 // Os argumentos são constantes ou valores baratos; o guard de logging não agrega ganho.
                    logger.LogInformation(
                        "Veto de aprovação {Operation} {Module} {Result} {Reason}",
                        Operation,
                        Module,
                        "vetoed",
                        "approval_vetoed");
#pragma warning restore CA1873
                    return AuthorizationResult<VetoApprovalResult>.Success(
                        new VetoApprovalResult(command.ApprovalId, command.ProposalId, VetoApprovalOutcome.Vetoed));
                }

                // Zero linhas: reconciliação dentro da mesma transação (RLS
                // continua aplicada pelo banco; filtros de consulta EF são
                // contornados para enxergar estados terminais).
                var current = await database.Approvals.AsNoTracking().IgnoreQueryFilters()
                    .SingleOrDefaultAsync(candidate =>
                        candidate.CompanyId == command.CompanyId && candidate.Id == command.ApprovalId,
                        cancellationToken);
                if (current is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return AuthorizationResult<VetoApprovalResult>.Failure(
                        AuthorizationErrorCode.ProposalUnavailable);
                }

                if (current.ProposalId != command.ProposalId)
                {
                    denial = AuthorizationErrorCode.ProposalConflict;
                }
                else if (current.IsConsumed)
                {
                    denial = AuthorizationErrorCode.ProposalConflict;
                }
                else if (current.IsVetoed)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return AuthorizationResult<VetoApprovalResult>.Success(
                        new VetoApprovalResult(
                            command.ApprovalId, command.ProposalId, VetoApprovalOutcome.AlreadyVetoed));
                }
                else if (current.IsExpired)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return AuthorizationResult<VetoApprovalResult>.Failure(
                        AuthorizationErrorCode.ProposalUnavailable);
                }
                else
                {
                    // Viva, correspondente e não terminal: o UPDATE só retorna
                    // zero por expiração no relógio do banco; qualquer outra
                    // hipótese é violação de invariante (a trava de linha da
                    // mesma transação impede mutação concorrente).
                    var databaseNow = await database.Database
                        .SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"")
                        .SingleAsync(cancellationToken);
                    await transaction.RollbackAsync(cancellationToken);
                    if (current.ExpiresAt <= databaseNow)
                    {
                        return AuthorizationResult<VetoApprovalResult>.Failure(
                            AuthorizationErrorCode.ProposalUnavailable);
                    }

                    throw new InvalidOperationException(
                        "Invariant breach: live matching approval was not vetoed by the conditional update.");
                }
            }

            await transaction.RollbackAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            return AuditUnavailableVeto(AuditUnavailablePhase.SuccessTransaction, exception);
        }

        return await ReturnAuditedVetoDenialAsync(actor, command.CompanyId, denial!.Value, cancellationToken);
    }

    private async Task<FoundationApproval?> ReloadApprovalAsync(
        ActorContext actor,
        Guid companyId,
        Guid approvalId,
        CancellationToken cancellationToken)
    {
        // Fora de uma transação ativa o contexto RLS (set_config is_local) já
        // não vigora; abre-se uma transação curta apenas para a leitura.
        await using var reloadTransaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await ApplyContextAsync(actor, companyId, cancellationToken);
        var approval = await database.Approvals.AsNoTracking().IgnoreQueryFilters()
            .SingleOrDefaultAsync(candidate =>
                candidate.CompanyId == companyId && candidate.Id == approvalId,
                cancellationToken);
        await reloadTransaction.RollbackAsync(cancellationToken);
        return approval;
    }

    private async Task<AuthorizationResult<ConfirmApprovalResult>> ReconcileConcurrentClaimAsync(
        ActorContext actor,
        ConfirmApprovalCommand command,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await ReloadApprovalAsync(actor, command.CompanyId, command.ApprovalId, cancellationToken);
            if (current is null)
            {
                return ProposalUnavailable("proposal_not_found_after_conflict");
            }

            if (MatchesReplay(current, command, payloadHash))
            {
                return AuthorizationResult<ConfirmApprovalResult>.Success(
                    new ConfirmApprovalResult(current.Id, current.ProposalId, ConfirmApprovalOutcome.Replay));
            }

            if (current.IsConsumed)
            {
                return await ReturnAuditedDenialAsync(
                    actor,
                    command.CompanyId,
                    AuthorizationErrorCode.ProposalConflict,
                    "proposal_already_consumed",
                    cancellationToken);
            }

            return ProposalUnavailable("proposal_expired_or_vetoed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return AuditUnavailable(AuditUnavailablePhase.ConcurrentReplay, exception);
        }
    }

    private AuthorizationResult<ConfirmApprovalResult> ProposalUnavailable(string reason)
    {
#pragma warning disable CA1873 // Os argumentos são constantes ou valores baratos; o guard de logging não agrega ganho.
        logger.LogInformation(
            "Confirmação de aprovação {Operation} {Module} {Result} {Reason}",
            Operation,
            Module,
            "unavailable",
            reason);
#pragma warning restore CA1873
        return AuthorizationResult<ConfirmApprovalResult>.Failure(AuthorizationErrorCode.ProposalUnavailable);
    }

    private async Task<AuthorizationResult<ConfirmApprovalResult>> ReturnAuditedDenialAsync(
        ActorContext actor,
        Guid companyId,
        AuthorizationErrorCode denial,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, companyId, cancellationToken);
            await auditAppender.AppendAsync(new AuditEventData(
                actor.UserId,
                AuditScope.Company,
                companyId,
                policy.AuditSource,
                denial.ToStableCode(),
                "approval",
                null,
                AuditOutcome.Denied,
                new CorrelationContext(actor.CorrelationId).CorrelationId,
                denial.ToStableCode(),
                "{}"), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (denial == AuthorizationErrorCode.ProposalConflict && IsExpectedSingleUseConflict(reason))
            {
#pragma warning disable CA1873 // Os argumentos são constantes ou valores baratos; o guard de logging não agrega ganho.
                logger.LogInformation(
                    "Confirmação de aprovação {Operation} {Module} {Result} {Reason}",
                    Operation,
                    Module,
                    "denied",
                    reason);
#pragma warning restore CA1873
            }
            else
            {
                logger.LogWarning(
                    "Confirmação de aprovação {Operation} {Module} {Result} {Reason}",
                    Operation,
                    Module,
                    "denied",
                    reason);
            }
            return AuthorizationResult<ConfirmApprovalResult>.Failure(denial);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return AuditUnavailable(AuditUnavailablePhase.DenialAudit, exception);
        }
    }

    private async Task<AuthorizationResult<VetoApprovalResult>> ReturnAuditedVetoDenialAsync(
        ActorContext actor,
        Guid companyId,
        AuthorizationErrorCode denial,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, companyId, cancellationToken);
            await auditAppender.AppendAsync(new AuditEventData(
                actor.UserId,
                AuditScope.Company,
                companyId,
                policy.AuditSource,
                denial.ToStableCode(),
                "approval",
                null,
                AuditOutcome.Denied,
                new CorrelationContext(actor.CorrelationId).CorrelationId,
                denial.ToStableCode(),
                "{}"), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AuthorizationResult<VetoApprovalResult>.Failure(denial);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return AuditUnavailableVeto(AuditUnavailablePhase.DenialAudit, exception);
        }
    }

    private AuthorizationResult<ConfirmApprovalResult> AuditUnavailable(
        AuditUnavailablePhase phase,
        Exception? exception = null)
    {
        logger.LogError(
            exception,
            "Confirmação de aprovação {Operation} {Module} {Result} {Reason}",
            Operation,
            Module,
            "unavailable",
            "database_or_audit_unavailable");
        telemetry.RecordAuditUnavailable(AuthorizationOperation.ApprovalConfirmation, phase);
        return AuthorizationResult<ConfirmApprovalResult>.Failure(AuthorizationErrorCode.AuditUnavailable);
    }

    private AuthorizationResult<VetoApprovalResult> AuditUnavailableVeto(
        AuditUnavailablePhase phase,
        Exception? exception = null)
    {
        logger.LogError(
            exception,
            "Veto de aprovação {Operation} {Module} {Result} {Reason}",
            Operation,
            Module,
            "unavailable",
            "database_or_audit_unavailable");
        telemetry.RecordAuditUnavailable(AuthorizationOperation.ApprovalConfirmation, phase);
        return AuthorizationResult<VetoApprovalResult>.Failure(AuthorizationErrorCode.AuditUnavailable);
    }

    private static bool MatchesReplay(
        FoundationApproval approval,
        ConfirmApprovalCommand command,
        string payloadHash) =>
        approval.IsConsumed
        && approval.ConfirmationRequestId == command.RequestId
        && string.Equals(approval.PayloadHash, payloadHash, StringComparison.Ordinal)
        && string.Equals(approval.PolicyVersion, command.PolicyVersion, StringComparison.Ordinal);

    private static bool IsExpectedSingleUseConflict(string reason) =>
        reason is "proposal_already_consumed" or "proposal_claim_lost";

    private Task ApplyContextAsync(ActorContext actor, Guid companyId, CancellationToken cancellationToken) =>
        FoundationRlsContext.ApplyCommandAsync(database.Database, actor, companyId, cancellationToken);
}
