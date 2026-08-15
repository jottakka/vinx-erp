using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Approvals;

/// <summary>
/// Preparação durável de uma aprovação de escrita do módulo AI (fase 1 do
/// fluxo de seis fases). Em uma única transação: autorização (membership
/// ativa + capability <c>ai.writes.confirm</c> em escopo company) →
/// idempotência (escopo PREPARATION) → varredura de expiração pelo relógio
/// do banco → verificação de proposta viva → INSERT da aprovação +
/// auditoria. Somente parsing estrutural do payload antecede a autorização;
/// validação semântica pertence ao módulo chamador e acontece depois.
/// </summary>
public sealed class PrepareApprovalService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    AuthorizationTelemetry telemetry,
    CapabilityEvaluator capabilityEvaluator,
    ILogger<PrepareApprovalService> logger)
{
    private const string CommandName = "ai.approvals.prepare";
    private const string ExpireCommandName = "ai.approvals.expire";
    private const string Operation = "approval_preparation";
    private const string Module = "foundation";
    private static readonly CapabilityRequirement RequiredCapability =
        new(KnownCapabilities.AiWritesConfirm, CapabilityScope.Company);

    public async Task<AuthorizationResult<PrepareApprovalResult>> PrepareAsync(
        ActorContext actor,
        PrepareApprovalCommand command,
        CancellationToken cancellationToken)
    {
        var payloadHash = CanonicalPayloadHasher.ComputeHash(command.Payload);
        var idempotencyHash = ComputeIdempotencyHash(command, payloadHash);
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
                var existing = await database.IdempotencyRecords.SingleOrDefaultAsync(record =>
                    record.CompanyId == command.CompanyId
                    && record.ActorUserId == actor.UserId
                    && record.CommandName == CommandName
                    && record.RequestId == command.RequestId,
                    cancellationToken);
                if (existing is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    if (!string.Equals(existing.PayloadHash, idempotencyHash, StringComparison.Ordinal))
                    {
                        return await ReturnAuditedDenialAsync(
                            actor,
                            command.CompanyId,
                            AuthorizationErrorCode.IdempotencyConflict,
                            cancellationToken);
                    }

                    var replay = JsonSerializer.Deserialize<PrepareApprovalResult>(existing.ResultJson);
                    return replay is null
                        ? AuditUnavailable(AuditUnavailablePhase.IdempotencyReplay)
                        : AuthorizationResult<PrepareApprovalResult>.Success(replay);
                }

                await SweepExpiredApprovalsAsync(actor, command.CompanyId, cancellationToken);

                // Substituição silenciosa é proibida: uma segunda preparação do
                // mesmo ator/empresa com proposta viva é negada (fail-closed).
                var liveProposalExists = await database.Approvals.AnyAsync(approval =>
                    approval.CompanyId == command.CompanyId
                    && approval.ActorUserId == actor.UserId
                    && approval.ConsumedAt == null,
                    cancellationToken);
                if (liveProposalExists)
                {
                    denial = AuthorizationErrorCode.ProposalConflict;
                }
                else
                {
                    var clock = await database.Database
                        .SqlQuery<ApprovalClock>($"""
                            SELECT now() AS created_at, (now() + {command.TimeToLive}) AS expires_at
                            """)
                        .SingleAsync(cancellationToken);
                    var approvalId = Guid.NewGuid();
                    var proposalId = Guid.NewGuid();
                    var result = new PrepareApprovalResult(approvalId, proposalId, clock.ExpiresAt);
                    database.Approvals.Add(new FoundationApproval(
                        approvalId,
                        command.CompanyId,
                        actor.UserId,
                        command.Action,
                        payloadHash,
                        command.PolicyVersion,
                        proposalId,
                        clock.CreatedAt,
                        clock.ExpiresAt,
                        command.RequestId));
                    database.IdempotencyRecords.Add(new IdempotencyRecord(
                        Guid.NewGuid(),
                        command.CompanyId,
                        actor.UserId,
                        CommandName,
                        command.RequestId,
                        idempotencyHash,
                        JsonSerializer.Serialize(result)));
                    await auditAppender.AppendAsync(new AuditEventData(
                        actor.UserId,
                        AuditScope.Company,
                        command.CompanyId,
                        AuditSource.Ai,
                        CommandName,
                        "approval",
                        approvalId,
                        AuditOutcome.Success,
                        new CorrelationContext(actor.CorrelationId).CorrelationId,
                        null,
                        "{}"), cancellationToken);
                    await database.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    logger.LogInformation(
                        "Preparação de aprovação {Operation} {Module} {Result} {Reason}",
                        Operation,
                        Module,
                        "prepared",
                        "durable_approval_created");
                    return AuthorizationResult<PrepareApprovalResult>.Success(result);
                }
            }

            await transaction.RollbackAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(
                exception,
                "Preparação de aprovação {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "retrying",
                "concurrent_write");
            database.ChangeTracker.Clear();
            var uniqueViolation = exception.InnerException as PostgresException is
            {
                SqlState: PostgresErrorCodes.UniqueViolation
            } postgresException
                ? ApprovalUniqueViolationClassifier.Classify(postgresException.ConstraintName)
                : UniqueViolationKind.Other;
            var proposalConflictWhenAbsent = uniqueViolation is UniqueViolationKind.LiveProposal
                or UniqueViolationKind.Idempotency;
            return await ReturnConcurrentReplayAsync(
                actor, command, idempotencyHash, proposalConflictWhenAbsent, cancellationToken);
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

        return await ReturnAuditedDenialAsync(actor, command.CompanyId, denial!.Value, cancellationToken);
    }

    /// <summary>
    /// Terminaliza logicamente propostas vivas vencidas do mesmo ator/empresa
    /// usando exclusivamente o relógio do banco (<c>now()</c>), dentro da mesma
    /// transação da preparação. A seleção trava as linhas (<c>FOR UPDATE</c>) e
    /// o corte único é o início da transação: uma proposta que vence no meio da
    /// transação não é varrida por ela. Falha de auditoria desfaz toda a
    /// preparação (fail-closed).
    /// </summary>
    private async Task SweepExpiredApprovalsAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        var expiredIds = await database.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value"
            FROM foundation.approvals
            WHERE company_id = {companyId}
              AND actor_user_id = {actor.UserId}
              AND consumed_at IS NULL
              AND deleted_at IS NULL
              AND expires_at <= now()
            FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (expiredIds.Count == 0)
        {
            return;
        }

        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE foundation.approvals
            SET deleted_at = now(),
                deleted_by_user_id = {actor.UserId},
                deleted_reason = 'approval_expired',
                updated_by_user_id = {actor.UserId},
                updated_at = now(),
                row_version = row_version + 1
            WHERE id = ANY({expiredIds.ToArray()})
            """, cancellationToken);

        foreach (var expiredId in expiredIds)
        {
            await auditAppender.AppendAsync(new AuditEventData(
                actor.UserId,
                AuditScope.Company,
                companyId,
                AuditSource.Ai,
                ExpireCommandName,
                "approval",
                expiredId,
                AuditOutcome.Success,
                new CorrelationContext(actor.CorrelationId).CorrelationId,
                null,
                "{}"), cancellationToken);
        }

        logger.LogInformation(
            "Varredura de expiração de aprovações {Operation} {Module} {Result} {Reason}",
            Operation,
            Module,
            "expired",
            "expired_live_proposals_terminalized");
    }

    private async Task<AuthorizationResult<PrepareApprovalResult>> ReturnAuditedDenialAsync(
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
                AuditSource.Ai,
                denial.ToStableCode(),
                "approval",
                null,
                AuditOutcome.Denied,
                new CorrelationContext(actor.CorrelationId).CorrelationId,
                denial.ToStableCode(),
                "{}"), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AuthorizationResult<PrepareApprovalResult>.Failure(denial);
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

    private async Task<AuthorizationResult<PrepareApprovalResult>> ReturnConcurrentReplayAsync(
        ActorContext actor,
        PrepareApprovalCommand command,
        string idempotencyHash,
        bool proposalConflictWhenAbsent,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            var record = await database.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(candidate =>
                candidate.CompanyId == command.CompanyId
                && candidate.ActorUserId == actor.UserId
                && candidate.CommandName == CommandName
                && candidate.RequestId == command.RequestId,
                cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            if (record is null)
            {
                return await ReturnAuditedDenialAsync(
                    actor,
                    command.CompanyId,
                    proposalConflictWhenAbsent
                        ? AuthorizationErrorCode.ProposalConflict
                        : AuthorizationErrorCode.IntegrityConflict,
                    cancellationToken);
            }

            if (!string.Equals(record.PayloadHash, idempotencyHash, StringComparison.Ordinal))
            {
                return await ReturnAuditedDenialAsync(
                    actor,
                    command.CompanyId,
                    AuthorizationErrorCode.IdempotencyConflict,
                    cancellationToken);
            }

            var replay = JsonSerializer.Deserialize<PrepareApprovalResult>(record.ResultJson);
            return replay is null
                ? AuditUnavailable(AuditUnavailablePhase.ConcurrentReplay)
                : AuthorizationResult<PrepareApprovalResult>.Success(replay);
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

    private AuthorizationResult<PrepareApprovalResult> AuditUnavailable(
        AuditUnavailablePhase phase,
        Exception? exception = null)
    {
        logger.LogError(
            exception,
            "Preparação de aprovação {Operation} {Module} {Result} {Reason}",
            Operation,
            Module,
            "unavailable",
            "database_or_audit_unavailable");
        telemetry.RecordAuditUnavailable(AuthorizationOperation.ApprovalPreparation, phase);
        return AuthorizationResult<PrepareApprovalResult>.Failure(AuthorizationErrorCode.AuditUnavailable);
    }

    private async Task ApplyContextAsync(ActorContext actor, Guid companyId, CancellationToken cancellationToken)
    {
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {actor.UserId.ToString()}, true)", cancellationToken);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {companyId.ToString()}, true)", cancellationToken);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.correlation_id', {new CorrelationContext(actor.CorrelationId).CorrelationId.ToString()}, true)", cancellationToken);
    }

    private static string ComputeIdempotencyHash(PrepareApprovalCommand command, string payloadHash) =>
        CanonicalPayloadHasher.ComputeHash(JsonSerializer.SerializeToElement(new
        {
            action = command.Action,
            policy_version = command.PolicyVersion,
            payload_hash = payloadHash,
        }));
}
