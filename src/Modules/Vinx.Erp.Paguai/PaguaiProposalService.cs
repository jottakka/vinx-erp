using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Configurations;

namespace Vinx.Erp.Paguai;

/// <summary>Persiste e lê somente a proposta tipada segura produzida pelo gate Pagaí.</summary>
public sealed class PaguaiProposalService(
    FoundationDbContext database,
    CapabilityEvaluator capabilityEvaluator,
    AuditAppender auditAppender,
    ILogger<PaguaiProposalService> logger)
{
    private static readonly CapabilityRequirement AiRead = new(KnownCapabilities.AiRead, CapabilityScope.Company);
    private static readonly CapabilityRequirement FilesReadOcr = new(KnownCapabilities.FilesReadOcr, CapabilityScope.Company);
    private static readonly CapabilityRequirement DraftCreate = new(KnownCapabilities.PaguaiDraftCreate, CapabilityScope.Company);

    public async Task<PaguaiProposalResult> CreateAsync(
        ActorContext actor,
        CreatePaguaiProposalCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryCanonicalize(command, out var canonicalSources, out var payloadHash))
        {
            return PaguaiProposalResult.Of(PaguaiProposalOperationState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            var databaseNow = await ReadDatabaseClockAsync(cancellationToken);
            var membership = await FindMembershipAsync(actor, command.CompanyId, cancellationToken);
            if (membership is null || !await HasRequiredCapabilitiesAsync(command.CompanyId, membership.Id, cancellationToken))
            {
                await AppendAuditAsync(actor, command.CompanyId, "paguai.proposal.create", null,
                    AuditOutcome.Denied, "capability_denied", AuditSource.Ai, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return PaguaiProposalResult.Of(PaguaiProposalOperationState.Forbidden);
            }

            var existing = await FindByRequestAsync(actor, command.CompanyId, command.RequestId, cancellationToken);
            if (existing is not null)
            {
                if (existing.ExpireIfDue(databaseNow, actor.UserId, command.RequestId))
                {
                    await AppendAuditAsync(actor, command.CompanyId, "paguai.proposal.expire", existing.Id,
                        AuditOutcome.Denied, "proposal_expired", AuditSource.Ai, cancellationToken);
                    await database.SaveChangesAsync(cancellationToken);
                }

                if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    await AppendAuditAsync(actor, command.CompanyId, "paguai.proposal.create", existing.Id,
                        AuditOutcome.Denied, "idempotency_conflict", AuditSource.Ai, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return PaguaiProposalResult.Of(PaguaiProposalOperationState.IdempotencyConflict);
                }

                await transaction.CommitAsync(cancellationToken);
                return new PaguaiProposalResult(PaguaiProposalOperationState.Replay, ToView(existing));
            }

            var file = await database.Files.SingleOrDefaultAsync(candidate =>
                candidate.CompanyId == command.CompanyId && candidate.Id == command.InvoiceFileId,
                cancellationToken);
            if (!IsEligibleAgenticFile(file, databaseNow))
            {
                await AppendAuditAsync(actor, command.CompanyId, "paguai.proposal.create", null,
                    AuditOutcome.Denied, "proposal_file_invalid", AuditSource.Ai, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return PaguaiProposalResult.Of(PaguaiProposalOperationState.Invalid);
            }

            var proposal = PaguaiDraftProposal.Create(
                Guid.NewGuid(), command.CompanyId, actor.UserId, command.RequestId, command.InvoiceFileId,
                command.Title, command.Description, PaguaiAmountRules.Canonicalize(command.Amount), command.Currency,
                command.DueDate, command.BeneficiaryDisplayName, command.ProofRequirement, command.Confidence,
                canonicalSources, command.PolicyVersion, payloadHash, databaseNow);
            database.PaguaiDraftProposals.Add(proposal);
            await database.SaveChangesAsync(cancellationToken);
            await AppendAuditAsync(actor, command.CompanyId, "paguai.proposal.create", proposal.Id,
                AuditOutcome.Success, null, AuditSource.Ai, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PaguaiProposalResult(PaguaiProposalOperationState.Created, ToView(proposal));
        }
        catch (DbUpdateException exception) when (IsLiveRequestRace(exception))
        {
            database.ChangeTracker.Clear();
            return await ReconcileCreateAsync(
                actor, command.CompanyId, command.RequestId, payloadHash, cancellationToken);
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
                "create", "paguai", "unavailable", "database_or_audit_unavailable");
            return PaguaiProposalResult.Of(PaguaiProposalOperationState.Unavailable);
        }
    }

    private async Task<PaguaiProposalResult> ReconcileCreateAsync(
        ActorContext actor,
        Guid companyId,
        Guid requestId,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, companyId, cancellationToken);
            var membership = await FindMembershipAsync(actor, companyId, cancellationToken);
            if (membership is null || !await HasRequiredCapabilitiesAsync(companyId, membership.Id, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return PaguaiProposalResult.Of(PaguaiProposalOperationState.Forbidden);
            }

            var existing = await FindByRequestAsync(actor, companyId, requestId, cancellationToken);
            if (existing is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return PaguaiProposalResult.Of(PaguaiProposalOperationState.Unavailable);
            }

            if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                await AppendAuditAsync(actor, companyId, "paguai.proposal.create", existing.Id,
                    AuditOutcome.Denied, "idempotency_conflict", AuditSource.Ai, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return PaguaiProposalResult.Of(PaguaiProposalOperationState.IdempotencyConflict);
            }

            await transaction.CommitAsync(cancellationToken);
            return new PaguaiProposalResult(PaguaiProposalOperationState.Replay, ToView(existing));
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
                "reconcile", "paguai", "unavailable", "proposal_reconciliation_failed");
            return PaguaiProposalResult.Of(PaguaiProposalOperationState.Unavailable);
        }
    }

    public async Task<PaguaiProposalResult> GetAsync(
        ActorContext actor,
        Guid companyId,
        Guid proposalId,
        CancellationToken cancellationToken)
    {
        if (companyId == Guid.Empty || proposalId == Guid.Empty)
        {
            return PaguaiProposalResult.Of(PaguaiProposalOperationState.NotFound);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, companyId, cancellationToken);
            var membership = await FindMembershipAsync(actor, companyId, cancellationToken);
            if (membership is null || !await HasRequiredCapabilitiesAsync(companyId, membership.Id, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return PaguaiProposalResult.Of(PaguaiProposalOperationState.NotFound);
            }

            var proposal = await database.PaguaiDraftProposals.SingleOrDefaultAsync(candidate =>
                candidate.CompanyId == companyId
                && candidate.ActorUserId == actor.UserId
                && candidate.Id == proposalId,
                cancellationToken);
            if (proposal is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return PaguaiProposalResult.Of(PaguaiProposalOperationState.NotFound);
            }

            var databaseNow = await ReadDatabaseClockAsync(cancellationToken);
            if (proposal.ExpireIfDue(databaseNow, actor.UserId, Guid.NewGuid()))
            {
                await AppendAuditAsync(actor, companyId, "paguai.proposal.expire", proposal.Id,
                    AuditOutcome.Denied, "proposal_expired", AuditSource.Web, cancellationToken);
                await database.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return proposal.State == PaguaiDraftProposal.PendingState
                ? new PaguaiProposalResult(PaguaiProposalOperationState.Found, ToView(proposal))
                : PaguaiProposalResult.Of(PaguaiProposalOperationState.ProposalUnavailable);
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
                "get", "paguai", "unavailable", "database_or_audit_unavailable");
            return PaguaiProposalResult.Of(PaguaiProposalOperationState.Unavailable);
        }
    }

    internal static bool IsEligibleAgenticFile(FileRecord? file, DateTimeOffset databaseNow) =>
        file is not null
        && file.ProcessingState == FileRecord.ConfirmedState
        && file.Classification == "agentic_attachment"
        && file.DeletedAt is null
        && file.RetentionUntil > databaseNow;

    internal static string ComputePayloadHash(CreatePaguaiProposalCommand command, string canonicalSources)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            command.CompanyId,
            command.RequestId,
            command.Title,
            command.Description,
            Amount = PaguaiAmountRules.Canonicalize(command.Amount),
            command.Currency,
            command.DueDate,
            command.BeneficiaryDisplayName,
            command.ProofRequirement,
            command.InvoiceFileId,
            command.Confidence,
            SourceReferences = canonicalSources,
            command.PolicyVersion,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    internal static PaguaiProposal ToView(PaguaiDraftProposal proposal) => new(
        proposal.Id,
        proposal.CompanyId,
        proposal.Title,
        proposal.Description,
        proposal.Amount,
        proposal.Currency,
        proposal.DueDate,
        proposal.BeneficiaryDisplayName,
        proposal.ProofRequirement,
        proposal.FileRecordId,
        proposal.Confidence,
        JsonSerializer.Deserialize<PaguaiProposalSource[]>(proposal.SourceReferences) ?? [],
        proposal.State,
        proposal.CreatedAt,
        proposal.ExpiresAt);

    private static bool TryCanonicalize(
        CreatePaguaiProposalCommand command,
        out string canonicalSources,
        out string payloadHash)
    {
        canonicalSources = string.Empty;
        payloadHash = string.Empty;
        if (command.CompanyId == Guid.Empty
            || command.RequestId == Guid.Empty
            || command.InvoiceFileId == Guid.Empty
            || !PaguaiAmountRules.IsValid(command.Amount)
            || command.SourceReferences is null
            || command.SourceReferences.Count != 3
            || string.IsNullOrWhiteSpace(command.PolicyVersion))
        {
            return false;
        }

        var requiredFields = new[] { "amount", "beneficiaryDisplayName", "dueDate" };
        var sources = command.SourceReferences.OrderBy(source => source.Field, StringComparer.Ordinal).ToArray();
        if (!sources.Select(source => source.Field).SequenceEqual(requiredFields, StringComparer.Ordinal)
            || sources.Any(source => string.IsNullOrWhiteSpace(source.Source)
                || !source.Source.StartsWith($"{command.InvoiceFileId:N}:p", StringComparison.Ordinal)
                || source.Source.Any(char.IsControl)))
        {
            return false;
        }

        try
        {
            canonicalSources = JsonSerializer.Serialize(sources);
            payloadHash = ComputePayloadHash(command, canonicalSources);
            _ = PaguaiDraftProposal.Create(
                Guid.NewGuid(), command.CompanyId, Guid.NewGuid(), command.RequestId, command.InvoiceFileId,
                command.Title, command.Description, PaguaiAmountRules.Canonicalize(command.Amount), command.Currency,
                command.DueDate, command.BeneficiaryDisplayName, command.ProofRequirement, command.Confidence,
                canonicalSources, command.PolicyVersion, payloadHash, DateTimeOffset.UtcNow);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private Task<Membership?> FindMembershipAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken) =>
        database.Memberships.SingleOrDefaultAsync(membership =>
            membership.CompanyId == companyId && membership.UserId == actor.UserId,
            cancellationToken);

    private Task<PaguaiDraftProposal?> FindByRequestAsync(
        ActorContext actor,
        Guid companyId,
        Guid requestId,
        CancellationToken cancellationToken) =>
        database.PaguaiDraftProposals
            .Where(proposal =>
                proposal.CompanyId == companyId
                && proposal.ActorUserId == actor.UserId
                && proposal.RequestId == requestId)
            .OrderBy(proposal => proposal.CreatedAt)
            .ThenBy(proposal => proposal.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private static bool IsLiveRequestRace(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgresException
        && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(
            postgresException.ConstraintName,
            PaguaiDraftProposalConfiguration.LiveRequestIndexName,
            StringComparison.Ordinal);

    private async Task<bool> HasRequiredCapabilitiesAsync(
        Guid companyId,
        Guid membershipId,
        CancellationToken cancellationToken) =>
        await capabilityEvaluator.HasGrantAsync(companyId, membershipId, AiRead, cancellationToken)
        && await capabilityEvaluator.HasGrantAsync(companyId, membershipId, FilesReadOcr, cancellationToken)
        && await capabilityEvaluator.HasGrantAsync(companyId, membershipId, DraftCreate, cancellationToken);

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
        Guid? resourceId,
        AuditOutcome outcome,
        string? reason,
        AuditSource source,
        CancellationToken cancellationToken) =>
        auditAppender.AppendAsync(new AuditEventData(
            actor.UserId, AuditScope.Company, companyId, source, action, "paguai_proposal",
            resourceId, outcome, actor.CorrelationId, reason, "{}"), cancellationToken);
}
