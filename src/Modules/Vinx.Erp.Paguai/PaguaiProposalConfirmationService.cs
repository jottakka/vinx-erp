using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Paguai;

/// <summary>Confirma ou rejeita uma proposta Pagaí sob uma única transação e reautorização atual.</summary>
public sealed class PaguaiProposalConfirmationService(
    FoundationDbContext database,
    CapabilityEvaluator capabilityEvaluator,
    AuditAppender auditAppender,
    ILogger<PaguaiProposalConfirmationService> logger)
{
    private const string ConfirmCommandName = "paguai.proposal.confirm";
    private const string RejectCommandName = "paguai.proposal.reject";
    private static readonly CapabilityRequirement AiRead = new(KnownCapabilities.AiRead, CapabilityScope.Company);
    private static readonly CapabilityRequirement FilesReadOcr = new(KnownCapabilities.FilesReadOcr, CapabilityScope.Company);
    private static readonly CapabilityRequirement DraftCreate = new(KnownCapabilities.PaguaiDraftCreate, CapabilityScope.Company);

    public async Task<PaguaiProposalConfirmationResult> ConfirmAsync(
        ActorContext actor,
        ConfirmPaguaiProposalCommand command,
        CancellationToken cancellationToken)
    {
        if (command.CompanyId == Guid.Empty || command.ProposalId == Guid.Empty || command.RequestId == Guid.Empty)
        {
            return PaguaiProposalConfirmationResult.Of(PaguaiProposalOperationState.Invalid);
        }

        var payloadHash = ComputeMutationHash(command.CompanyId, command.ProposalId);
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            var proposal = await LockProposalAsync(actor, command.CompanyId, command.ProposalId, cancellationToken);
            if (proposal is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return PaguaiProposalConfirmationResult.Of(PaguaiProposalOperationState.ProposalUnavailable);
            }

            var databaseNow = await ReadDatabaseClockAsync(cancellationToken);
            if (proposal.ExpireIfDue(databaseNow, actor.UserId, command.RequestId))
            {
                await AppendAuditAsync(actor, command.CompanyId, ConfirmCommandName, proposal.Id,
                    AuditOutcome.Denied, "proposal_expired", cancellationToken);
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return PaguaiProposalConfirmationResult.Of(PaguaiProposalOperationState.ProposalUnavailable);
            }

            var membership = await FindMembershipAsync(actor, command.CompanyId, cancellationToken);
            if (membership is null || !await HasRequiredCapabilitiesAsync(command.CompanyId, membership.Id, cancellationToken))
            {
                return await InvalidateConfirmationAsync(
                    transaction, actor, command, proposal, databaseNow, "capability_denied", cancellationToken);
            }

            var receipt = await FindReceiptAsync(actor, command.CompanyId, ConfirmCommandName, command.RequestId, cancellationToken);
            if (receipt is not null)
            {
                if (!string.Equals(receipt.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    await AppendAuditAsync(actor, command.CompanyId, ConfirmCommandName, proposal.Id,
                        AuditOutcome.Denied, "idempotency_conflict", cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return PaguaiProposalConfirmationResult.Of(PaguaiProposalOperationState.IdempotencyConflict);
                }

                await transaction.CommitAsync(cancellationToken);
                return DeserializeConfirmationReplay(receipt.ResultJson);
            }

            if (proposal.State != PaguaiDraftProposal.PendingState
                || !PayloadHashMatchesProposal(proposal))
            {
                if (proposal.State == PaguaiDraftProposal.PendingState)
                {
                    return await InvalidateConfirmationAsync(
                        transaction, actor, command, proposal, databaseNow, "proposal_integrity_invalid", cancellationToken);
                }

                await transaction.RollbackAsync(cancellationToken);
                return PaguaiProposalConfirmationResult.Of(PaguaiProposalOperationState.ProposalUnavailable);
            }

            var file = await database.Files.SingleOrDefaultAsync(candidate =>
                candidate.CompanyId == command.CompanyId && candidate.Id == proposal.FileRecordId,
                cancellationToken);
            if (!PaguaiProposalService.IsEligibleAgenticFile(file, databaseNow))
            {
                return await InvalidateConfirmationAsync(
                    transaction, actor, command, proposal, databaseNow, "proposal_file_invalid", cancellationToken);
            }

            var draftCommand = new CreateDraftCommand(
                command.CompanyId,
                command.RequestId,
                proposal.Title,
                proposal.Description,
                proposal.Amount,
                proposal.Currency,
                proposal.DueDate,
                proposal.BeneficiaryDisplayName,
                proposal.ProofRequirement,
                SourceInboundMessageId: null,
                InvoiceFileId: proposal.FileRecordId);
            var request = PaguaiDraftService.BuildDraftRequest(
                actor, draftCommand, Guid.NewGuid(), proposal.Id);
            if (request is null)
            {
                return await InvalidateConfirmationAsync(
                    transaction, actor, command, proposal, databaseNow, "proposal_integrity_invalid", cancellationToken);
            }

            file!.PromoteAgenticAttachmentToPaguaiInvoice(actor.UserId, command.RequestId, databaseNow);
            database.PaguaiRequests.Add(request);
            await database.SaveChangesAsync(cancellationToken);
            var draft = PaguaiDraftService.ToDraft(request);
            proposal.Consume(draft.Id, actor.UserId, command.RequestId, databaseNow);
            database.IdempotencyRecords.Add(new IdempotencyRecord(
                Guid.NewGuid(), command.CompanyId, actor.UserId, ConfirmCommandName,
                command.RequestId, payloadHash, JsonSerializer.Serialize(draft)));
            await AppendAuditAsync(actor, command.CompanyId, ConfirmCommandName, proposal.Id,
                AuditOutcome.Success, null, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PaguaiProposalConfirmationResult(PaguaiProposalOperationState.Created, draft);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            logger.LogError(exception,
                "Paguai proposal {Operation} {Module} {Result} {Reason}",
                "confirm", "paguai", "unavailable", "database_or_audit_unavailable");
            return PaguaiProposalConfirmationResult.Of(PaguaiProposalOperationState.Unavailable);
        }
    }

    public async Task<PaguaiProposalMutationResult> RejectAsync(
        ActorContext actor,
        RejectPaguaiProposalCommand command,
        CancellationToken cancellationToken)
    {
        if (command.CompanyId == Guid.Empty || command.ProposalId == Guid.Empty || command.RequestId == Guid.Empty)
        {
            return PaguaiProposalMutationResult.Of(PaguaiProposalOperationState.Invalid);
        }

        var payloadHash = ComputeMutationHash(command.CompanyId, command.ProposalId);
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            var proposal = await LockProposalAsync(actor, command.CompanyId, command.ProposalId, cancellationToken);
            if (proposal is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return PaguaiProposalMutationResult.Of(PaguaiProposalOperationState.ProposalUnavailable);
            }

            var databaseNow = await ReadDatabaseClockAsync(cancellationToken);
            if (proposal.ExpireIfDue(databaseNow, actor.UserId, command.RequestId))
            {
                await AppendAuditAsync(actor, command.CompanyId, RejectCommandName, proposal.Id,
                    AuditOutcome.Denied, "proposal_expired", cancellationToken);
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return PaguaiProposalMutationResult.Of(PaguaiProposalOperationState.ProposalUnavailable);
            }

            var membership = await FindMembershipAsync(actor, command.CompanyId, cancellationToken);
            if (membership is null || !await HasRequiredCapabilitiesAsync(command.CompanyId, membership.Id, cancellationToken))
            {
                if (proposal.State == PaguaiDraftProposal.PendingState)
                {
                    proposal.Invalidate(actor.UserId, command.RequestId, databaseNow);
                    await AppendAuditAsync(actor, command.CompanyId, RejectCommandName, proposal.Id,
                        AuditOutcome.Denied, "capability_denied", cancellationToken);
                    await database.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                else
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return PaguaiProposalMutationResult.Of(PaguaiProposalOperationState.ProposalUnavailable);
            }

            var receipt = await FindReceiptAsync(actor, command.CompanyId, RejectCommandName, command.RequestId, cancellationToken);
            if (receipt is not null)
            {
                if (!string.Equals(receipt.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    await AppendAuditAsync(actor, command.CompanyId, RejectCommandName, proposal.Id,
                        AuditOutcome.Denied, "idempotency_conflict", cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return PaguaiProposalMutationResult.Of(PaguaiProposalOperationState.IdempotencyConflict);
                }

                await transaction.CommitAsync(cancellationToken);
                return PaguaiProposalMutationResult.Of(PaguaiProposalOperationState.Replay);
            }

            if (proposal.State != PaguaiDraftProposal.PendingState)
            {
                await transaction.RollbackAsync(cancellationToken);
                return PaguaiProposalMutationResult.Of(PaguaiProposalOperationState.ProposalUnavailable);
            }

            proposal.Reject(actor.UserId, command.RequestId, databaseNow);
            database.IdempotencyRecords.Add(new IdempotencyRecord(
                Guid.NewGuid(), command.CompanyId, actor.UserId, RejectCommandName,
                command.RequestId, payloadHash, "{}"));
            await AppendAuditAsync(actor, command.CompanyId, RejectCommandName, proposal.Id,
                AuditOutcome.Success, null, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PaguaiProposalMutationResult.Of(PaguaiProposalOperationState.Created);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            logger.LogError(exception,
                "Paguai proposal {Operation} {Module} {Result} {Reason}",
                "reject", "paguai", "unavailable", "database_or_audit_unavailable");
            return PaguaiProposalMutationResult.Of(PaguaiProposalOperationState.Unavailable);
        }
    }

    private async Task<PaguaiProposalConfirmationResult> InvalidateConfirmationAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        ActorContext actor,
        ConfirmPaguaiProposalCommand command,
        PaguaiDraftProposal proposal,
        DateTimeOffset databaseNow,
        string reason,
        CancellationToken cancellationToken)
    {
        if (proposal.State == PaguaiDraftProposal.PendingState)
        {
            proposal.Invalidate(actor.UserId, command.RequestId, databaseNow);
            await AppendAuditAsync(actor, command.CompanyId, ConfirmCommandName, proposal.Id,
                AuditOutcome.Denied, reason, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }

        return PaguaiProposalConfirmationResult.Of(PaguaiProposalOperationState.ProposalUnavailable);
    }

    private Task<PaguaiDraftProposal?> LockProposalAsync(
        ActorContext actor,
        Guid companyId,
        Guid proposalId,
        CancellationToken cancellationToken) =>
        database.PaguaiDraftProposals
            .FromSqlInterpolated($"""
                SELECT * FROM paguai.draft_proposals
                WHERE company_id = {companyId}
                  AND actor_user_id = {actor.UserId}
                  AND id = {proposalId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

    private Task<Membership?> FindMembershipAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken) =>
        database.Memberships.SingleOrDefaultAsync(membership =>
            membership.CompanyId == companyId && membership.UserId == actor.UserId,
            cancellationToken);

    private async Task<bool> HasRequiredCapabilitiesAsync(
        Guid companyId,
        Guid membershipId,
        CancellationToken cancellationToken) =>
        await capabilityEvaluator.HasGrantAsync(companyId, membershipId, AiRead, cancellationToken)
        && await capabilityEvaluator.HasGrantAsync(companyId, membershipId, FilesReadOcr, cancellationToken)
        && await capabilityEvaluator.HasGrantAsync(companyId, membershipId, DraftCreate, cancellationToken);

    private Task<IdempotencyRecord?> FindReceiptAsync(
        ActorContext actor,
        Guid companyId,
        string commandName,
        Guid requestId,
        CancellationToken cancellationToken) =>
        database.IdempotencyRecords.SingleOrDefaultAsync(receipt =>
            receipt.CompanyId == companyId
            && receipt.ActorUserId == actor.UserId
            && receipt.CommandName == commandName
            && receipt.RequestId == requestId,
            cancellationToken);

    private static PaguaiProposalConfirmationResult DeserializeConfirmationReplay(string resultJson)
    {
        try
        {
            return JsonSerializer.Deserialize<PaguaiDraft>(resultJson) is { } draft
                ? new PaguaiProposalConfirmationResult(PaguaiProposalOperationState.Replay, draft)
                : PaguaiProposalConfirmationResult.Of(PaguaiProposalOperationState.Unavailable);
        }
        catch (JsonException)
        {
            return PaguaiProposalConfirmationResult.Of(PaguaiProposalOperationState.Unavailable);
        }
    }

    private static bool PayloadHashMatchesProposal(PaguaiDraftProposal proposal)
    {
        try
        {
            var sources = JsonSerializer.Deserialize<PaguaiProposalSource[]>(proposal.SourceReferences) ?? [];
            if (!PaguaiProposalService.TrySerializeCanonicalSources(
                    proposal.FileRecordId,
                    sources,
                    out var canonicalSources))
            {
                return false;
            }

            var command = new CreatePaguaiProposalCommand(
                proposal.CompanyId,
                proposal.RequestId,
                proposal.Title,
                proposal.Description,
                proposal.Amount,
                proposal.Currency,
                proposal.DueDate,
                proposal.BeneficiaryDisplayName,
                proposal.ProofRequirement,
                proposal.FileRecordId,
                proposal.Confidence,
                sources,
                proposal.PolicyVersion);
            return string.Equals(
                proposal.PayloadHash,
                PaguaiProposalService.ComputePayloadHash(command, canonicalSources),
                StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ComputeMutationHash(Guid companyId, Guid proposalId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { companyId, proposalId })))).ToLowerInvariant();

    private Task<DateTimeOffset> ReadDatabaseClockAsync(CancellationToken cancellationToken) =>
        database.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(cancellationToken);

    private Task ApplyContextAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken) =>
        FoundationRlsContext.ApplyCommandAsync(database.Database, actor, companyId, cancellationToken);

    private Task AppendAuditAsync(
        ActorContext actor,
        Guid companyId,
        string action,
        Guid proposalId,
        AuditOutcome outcome,
        string? reason,
        CancellationToken cancellationToken) =>
        auditAppender.AppendAsync(new AuditEventData(
            actor.UserId, AuditScope.Company, companyId, AuditSource.Web, action,
            "paguai_proposal", proposalId, outcome, actor.CorrelationId, reason, "{}"), cancellationToken);
}
