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
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Configurations;

namespace Vinx.Erp.Atendimento;

/// <summary>
/// Regras do cartão fixo de Atendimento. Foundation fornece apenas transação, RLS, auditoria e idempotência compartilhados.
/// </summary>
public sealed class AtendimentoCardService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    CapabilityEvaluator capabilityEvaluator,
    ILogger<AtendimentoCardService> logger
)
{
    private const string Module = "service_desk";
    private const string Operation = "atendimento_card";
    private const string ResourceType = "atendimento_card";
    private const string ReadQueueAction = "service_desk.ticket.read_queue";
    private const string CreateAction = "service_desk.ticket.create";
    private const string AssignAction = "service_desk.ticket.assign";
    private const string MoveAction = "service_desk.ticket.move";
    private const string CancelAction = "service_desk.ticket.cancel";
    private const string AssignmentOptionsReadAction = "service_desk.ticket.assignment_options.read";

    public Task<AtendimentoCardOperationResult> CreateAsync(
        ActorContext actor,
        CreateAtendimentoCardCommand command,
        CancellationToken cancellationToken
    )
    {
        var normalized = NormalizeCreateCommand(command);
        return ExecuteAsync(
            actor,
            normalized.CompanyId,
            normalized.RequestId,
            KnownCapabilities.ServiceDeskTicketCreate,
            CreateAction,
            CreateAction,
            IsValid(normalized),
            HashCreate(normalized),
            async () =>
            {
                if (
                    !await HasConfirmedAttachmentAsync(
                        normalized.CompanyId,
                        normalized.AttachmentFileId,
                        cancellationToken
                    )
                    || !await HasActiveParticipantsAsync(
                        normalized.CompanyId,
                        normalized.ParticipantMembershipIds!,
                        cancellationToken
                    )
                )
                {
                    return Mutation.ResourceNotFound();
                }

                var card = await InsertCardAsync(actor, normalized, cancellationToken);
                return Mutation.Succeeded(card, AtendimentoCardOperationState.Created);
            },
            cancellationToken
        );
    }

    public Task<AtendimentoCardOperationResult> AssignAsync(
        ActorContext actor,
        Guid cardId,
        AssignAtendimentoCardCommand command,
        CancellationToken cancellationToken
    ) =>
        ExecuteAsync(
            actor,
            command.CompanyId,
            command.RequestId,
            KnownCapabilities.ServiceDeskTicketAssign,
            AssignAction,
            CommandScope(AssignAction, cardId),
            IsValid(command),
            Hash(new RoutedCommandPayload<AssignAtendimentoCardCommand>(cardId, command)),
            async () =>
            {
                var before = await FindCardAsync(command.CompanyId, cardId, cancellationToken);
                if (before is null)
                {
                    return Mutation.ResourceNotFound();
                }

                if (IsCancelled(before))
                {
                    return Mutation.IntegrityConflict(before);
                }

                if (
                    !await IsActiveMembershipAsync(
                        command.CompanyId,
                        command.AssigneeMembershipId,
                        cancellationToken
                    )
                )
                {
                    return Mutation.ResourceNotFound();
                }

                var updated = await UpdateAssigneeAsync(actor, cardId, command, cancellationToken);
                if (updated is not null)
                {
                    return Mutation.Succeeded(updated, AtendimentoCardOperationState.Success);
                }

                return await ReadCurrentAfterMutationMissAsync(
                    command.CompanyId,
                    cardId,
                    cancellationToken
                );
            },
            cancellationToken
        );

    public Task<AtendimentoCardOperationResult> MoveAsync(
        ActorContext actor,
        Guid cardId,
        MoveAtendimentoCardCommand command,
        CancellationToken cancellationToken
    ) =>
        ExecuteAsync(
            actor,
            command.CompanyId,
            command.RequestId,
            KnownCapabilities.ServiceDeskTicketMove,
            MoveAction,
            CommandScope(MoveAction, cardId),
            IsValid(command),
            Hash(new RoutedCommandPayload<MoveAtendimentoCardCommand>(cardId, command)),
            async () =>
            {
                var before = await FindCardAsync(command.CompanyId, cardId, cancellationToken);
                if (before is null)
                {
                    return Mutation.ResourceNotFound();
                }

                if (IsCancelled(before))
                {
                    return Mutation.IntegrityConflict(before);
                }

                var updated = await MoveCardAsync(actor, before.Id, command, cancellationToken);
                if (updated is null)
                {
                    return await ReadCurrentAfterMutationMissAsync(
                        command.CompanyId,
                        cardId,
                        cancellationToken
                    );
                }

                await database.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO service_desk.atendimento_card_history
                        (id, company_id, card_id, from_column, to_column, actor_user_id, request_id)
                    VALUES ({Guid.NewGuid()}, {command.CompanyId}, {cardId}, {before.Column}, {command.Column.Trim()}, {actor.UserId}, {command.RequestId})
                    """,
                    cancellationToken
                );
                return Mutation.Succeeded(updated, AtendimentoCardOperationState.Success);
            },
            cancellationToken
        );

    public Task<AtendimentoCardOperationResult> CancelAsync(
        ActorContext actor,
        Guid cardId,
        CancelAtendimentoCardCommand command,
        CancellationToken cancellationToken
    )
    {
        var normalized = command with { Reason = command.Reason?.Trim() ?? string.Empty };
        return ExecuteAsync(
            actor,
            normalized.CompanyId,
            normalized.RequestId,
            KnownCapabilities.ServiceDeskTicketCancel,
            CancelAction,
            CommandScope(CancelAction, cardId),
            IsValid(normalized),
            Hash(new RoutedCommandPayload<CancelAtendimentoCardCommand>(cardId, normalized)),
            async () =>
            {
                var before = await FindCardAsync(normalized.CompanyId, cardId, cancellationToken);
                if (before is null)
                {
                    return Mutation.ResourceNotFound();
                }

                if (IsCancelled(before))
                {
                    return Mutation.Terminal(before);
                }

                var updated = await CancelCardAsync(actor, cardId, normalized, cancellationToken);
                if (updated is null)
                {
                    return await ReadCurrentAfterMutationMissAsync(
                        normalized.CompanyId,
                        cardId,
                        cancellationToken
                    );
                }

                await database.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO service_desk.atendimento_card_history
                        (id, company_id, card_id, from_column, to_column, actor_user_id, request_id)
                    VALUES ({Guid.NewGuid()}, {normalized.CompanyId}, {cardId}, {before.Column}, {"cancelado"}, {actor.UserId}, {normalized.RequestId})
                    """,
                    cancellationToken
                );
                return Mutation.Succeeded(updated, AtendimentoCardOperationState.Success);
            },
            cancellationToken
        );
    }

    public async Task<AtendimentoCardOperationResult> GetQueueAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    )
    {
        if (companyId == Guid.Empty)
        {
            return new(AtendimentoCardOperationState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await SetContextAsync(actor, companyId, cancellationToken);
            var denied = await AuthorizeAsync(
                actor,
                companyId,
                KnownCapabilities.ServiceDeskTicketReadQueue,
                cancellationToken
            );
            if (denied is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await AuditDeniedResultAsync(
                    actor,
                    companyId,
                    ReadQueueAction,
                    denied.Value,
                    cancellationToken
                );
            }

            var cards = await QueryCardsAsync(companyId, cancellationToken);
            await AppendAuditAsync(
                actor,
                companyId,
                ReadQueueAction,
                null,
                AuditOutcome.Success,
                null,
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            LogInformation("succeeded", "queue_read");
            return new(AtendimentoCardOperationState.Success, Cards: cards);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "queue_read_failed");
            return new(AtendimentoCardOperationState.Unavailable);
        }
    }

    public async Task<AtendimentoCardOperationResult> GetAssignmentOptionsAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    )
    {
        if (companyId == Guid.Empty)
        {
            return new(AtendimentoCardOperationState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await SetContextAsync(actor, companyId, cancellationToken);
            var denied = await AuthorizeAsync(
                actor,
                companyId,
                KnownCapabilities.ServiceDeskTicketAssign,
                cancellationToken
            );
            if (denied is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await AuditDeniedResultAsync(
                    actor,
                    companyId,
                    AssignmentOptionsReadAction,
                    denied.Value,
                    cancellationToken
                );
            }

            var options = await database.Database.SqlQuery<AtendimentoAssignmentOptionResponse>(
                $"SELECT membership_id, display_name FROM foundation.current_company_membership_options()"
            ).ToListAsync(cancellationToken);
            await AppendAuditAsync(
                actor,
                companyId,
                AssignmentOptionsReadAction,
                null,
                AuditOutcome.Success,
                null,
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            LogInformation("succeeded", "assignment_options_read");
            return new(AtendimentoCardOperationState.Success, AssignmentOptions: options);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "assignment_options_read_failed");
            return new(AtendimentoCardOperationState.Unavailable);
        }
    }

    private async Task<AtendimentoCardOperationResult> ExecuteAsync(
        ActorContext actor,
        Guid companyId,
        Guid requestId,
        string capability,
        string auditAction,
        string idempotencyCommandName,
        bool valid,
        string payloadHash,
        Func<Task<Mutation>> mutation,
        CancellationToken cancellationToken
    )
    {
        if (companyId == Guid.Empty || requestId == Guid.Empty || !valid)
        {
            return new(AtendimentoCardOperationState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await SetContextAsync(actor, companyId, cancellationToken);
            var denied = await AuthorizeAsync(actor, companyId, capability, cancellationToken);
            if (denied is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await AuditDeniedResultAsync(
                    actor,
                    companyId,
                    auditAction,
                    denied.Value,
                    cancellationToken
                );
            }

            var existing = await FindIdempotencyRecordAsync(
                companyId,
                actor.UserId,
                idempotencyCommandName,
                requestId,
                cancellationToken
            );
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    return await AuditDeniedResultAsync(
                        actor,
                        companyId,
                        auditAction,
                        AtendimentoCardOperationState.IdempotencyConflict,
                        cancellationToken
                    );
                }

                var replay = JsonSerializer.Deserialize<AtendimentoCardResponse>(
                    existing.ResultJson
                );
                if (replay is null)
                {
                    LogUnavailable(null, "idempotency_result_invalid");
                    return new(AtendimentoCardOperationState.Unavailable);
                }

                LogInformation("replayed", "idempotency_match");
                return new(AtendimentoCardOperationState.Replay, NormalizeResponse(replay));
            }

            var result = await mutation();
            if (result.State is AtendimentoCardOperationState.ResourceNotFound)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await AuditDeniedResultAsync(
                    actor,
                    companyId,
                    auditAction,
                    result.State,
                    cancellationToken
                );
            }

            if (
                result.State
                is AtendimentoCardOperationState.IntegrityConflict
                    or AtendimentoCardOperationState.TerminalState
            )
            {
                await transaction.RollbackAsync(cancellationToken);
                var audited = await AuditDeniedResultAsync(
                    actor,
                    companyId,
                    auditAction,
                    result.State,
                    cancellationToken
                );
                return audited.State == result.State
                    ? new(result.State, Current: result.Current)
                    : audited;
            }

            database.IdempotencyRecords.Add(
                new IdempotencyRecord(
                    Guid.NewGuid(),
                    companyId,
                    actor.UserId,
                    idempotencyCommandName,
                    requestId,
                    payloadHash,
                    JsonSerializer.Serialize(result.Card)
                )
            );
            await AppendAuditAsync(
                actor,
                companyId,
                auditAction,
                result.Card!.Id,
                AuditOutcome.Success,
                null,
                cancellationToken
            );
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogInformation("succeeded", "command_completed");
            return new(result.State, result.Card);
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            database.ChangeTracker.Clear();
            logger.LogWarning(
                exception,
                "Atendimento {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "retrying",
                "idempotency_unique_conflict"
            );
            return await ResolveConcurrentReplayAsync(
                actor,
                companyId,
                requestId,
                auditAction,
                idempotencyCommandName,
                capability,
                payloadHash,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "database_or_audit_failed");
            return new(AtendimentoCardOperationState.Unavailable);
        }
    }

    private async Task<AtendimentoCardOperationResult> ResolveConcurrentReplayAsync(
        ActorContext actor,
        Guid companyId,
        Guid requestId,
        string auditAction,
        string idempotencyCommandName,
        string capability,
        string payloadHash,
        CancellationToken cancellationToken
    )
    {
        try
        {
            IdempotencyRecord? record = null;
            AtendimentoCardOperationState? denial = null;
            await using (
                var transaction = await database.Database.BeginTransactionAsync(cancellationToken)
            )
            {
                await SetContextAsync(actor, companyId, cancellationToken);
                denial = await AuthorizeAsync(actor, companyId, capability, cancellationToken);
                if (denial is null)
                {
                    record = await FindIdempotencyRecordAsync(
                        companyId,
                        actor.UserId,
                        idempotencyCommandName,
                        requestId,
                        cancellationToken
                    );
                }

                await transaction.RollbackAsync(cancellationToken);
            }

            if (denial is not null)
            {
                return await AuditDeniedResultAsync(
                    actor,
                    companyId,
                    auditAction,
                    denial.Value,
                    cancellationToken
                );
            }

            if (record is null)
            {
                return await AuditDeniedResultAsync(
                    actor,
                    companyId,
                    auditAction,
                    AtendimentoCardOperationState.IntegrityConflict,
                    cancellationToken
                );
            }

            if (!string.Equals(record.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return await AuditDeniedResultAsync(
                    actor,
                    companyId,
                    auditAction,
                    AtendimentoCardOperationState.IdempotencyConflict,
                    cancellationToken
                );
            }

            var replay = JsonSerializer.Deserialize<AtendimentoCardResponse>(record.ResultJson);
            if (replay is null)
            {
                LogUnavailable(null, "idempotency_result_invalid");
                return new(AtendimentoCardOperationState.Unavailable);
            }

            LogInformation("replayed", "concurrent_idempotency_match");
            return new(AtendimentoCardOperationState.Replay, NormalizeResponse(replay));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "idempotency_reconciliation_failed");
            return new(AtendimentoCardOperationState.Unavailable);
        }
    }

    private async Task<AtendimentoCardOperationResult> AuditDeniedResultAsync(
        ActorContext actor,
        Guid companyId,
        string auditAction,
        AtendimentoCardOperationState state,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await SetContextAsync(actor, companyId, cancellationToken);
            var reason = ErrorCode(state);
            await AppendAuditAsync(
                actor,
                companyId,
                auditAction,
                null,
                AuditOutcome.Denied,
                reason,
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            LogInformation("denied", reason);
            return new(state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "denial_audit_failed");
            return new(AtendimentoCardOperationState.Unavailable);
        }
    }

    private async Task<AtendimentoCardOperationState?> AuthorizeAsync(
        ActorContext actor,
        Guid companyId,
        string capability,
        CancellationToken cancellationToken
    )
    {
        var membership = await database.Memberships.SingleOrDefaultAsync(
            candidate => candidate.CompanyId == companyId && candidate.UserId == actor.UserId,
            cancellationToken
        );
        if (membership is null)
        {
            return AtendimentoCardOperationState.MembershipRequired;
        }

        return await capabilityEvaluator.HasGrantAsync(
            companyId,
            membership.Id,
            new CapabilityRequirement(capability, CapabilityScope.Company),
            cancellationToken
        )
            ? null
            : AtendimentoCardOperationState.CapabilityDenied;
    }

    private Task<IdempotencyRecord?> FindIdempotencyRecordAsync(
        Guid companyId,
        Guid actorUserId,
        string commandName,
        Guid requestId,
        CancellationToken cancellationToken
    ) =>
        database.IdempotencyRecords.SingleOrDefaultAsync(
            record =>
                record.CompanyId == companyId
                && record.ActorUserId == actorUserId
                && record.CommandName == commandName
                && record.RequestId == requestId,
            cancellationToken
        );

    private Task<bool> IsActiveMembershipAsync(
        Guid companyId,
        Guid membershipId,
        CancellationToken cancellationToken
    ) =>
        database.Memberships.AnyAsync(
            candidate => candidate.CompanyId == companyId && candidate.Id == membershipId,
            cancellationToken
        );

    private async Task<bool> HasConfirmedAttachmentAsync(
        Guid companyId,
        Guid? attachmentFileId,
        CancellationToken cancellationToken
    ) =>
        attachmentFileId is null
        || await database.Files.AsNoTracking().AnyAsync(
            file =>
                file.CompanyId == companyId
                && file.Id == attachmentFileId.Value
                && file.Classification == "service_desk_attachment"
                && file.ProcessingState == FileRecord.ConfirmedState,
            cancellationToken
        );

    private async Task<bool> HasActiveParticipantsAsync(
        Guid companyId,
        IReadOnlyList<Guid> participantMembershipIds,
        CancellationToken cancellationToken
    )
    {
        if (participantMembershipIds.Count == 0)
        {
            return true;
        }

        var activeCount = await database.Memberships.CountAsync(
            membership =>
                membership.CompanyId == companyId
                && participantMembershipIds.Contains(membership.Id),
            cancellationToken
        );
        return activeCount == participantMembershipIds.Count;
    }

    private async Task<Mutation> ReadCurrentAfterMutationMissAsync(
        Guid companyId,
        Guid cardId,
        CancellationToken cancellationToken
    )
    {
        var current = await FindCardAsync(companyId, cardId, cancellationToken);
        return current is null ? Mutation.ResourceNotFound() : Mutation.IntegrityConflict(current);
    }

    private Task SetContextAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    ) => FoundationRlsContext.ApplyCommandAsync(
        database.Database,
        actor,
        companyId,
        cancellationToken);

    private Task AppendAuditAsync(
        ActorContext actor,
        Guid companyId,
        string action,
        Guid? resourceId,
        AuditOutcome outcome,
        string? reason,
        CancellationToken cancellationToken
    ) =>
        auditAppender.AppendAsync(
            new AuditEventData(
                actor.UserId,
                AuditScope.Company,
                companyId,
                AuditSource.Web,
                action,
                ResourceType,
                resourceId,
                outcome,
                new CorrelationContext(actor.CorrelationId).CorrelationId,
                reason,
                "{}"
            ),
            cancellationToken
        );

    private async Task<List<AtendimentoCardResponse>> QueryCardsAsync(
        Guid companyId,
        CancellationToken cancellationToken
    )
    {
        var cards = await database
            .AtendimentoCards.AsNoTracking()
            .Include(card => card.Participants)
            .Where(card => card.CompanyId == companyId)
            .OrderBy(card => card.CreatedAt)
            .ToListAsync(cancellationToken);
        return [.. cards.Select(ToResponse)];
    }

    private async Task<AtendimentoCardResponse?> FindCardAsync(
        Guid companyId,
        Guid cardId,
        CancellationToken cancellationToken
    )
    {
        var card = await database
            .AtendimentoCards.AsNoTracking()
            .Include(card => card.Participants)
            .SingleOrDefaultAsync(
                card => card.CompanyId == companyId && card.Id == cardId,
                cancellationToken
            );
        return card is null ? null : ToResponse(card);
    }

    private async Task<AtendimentoCardResponse> InsertCardAsync(
        ActorContext actor,
        CreateAtendimentoCardCommand command,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        var card = new AtendimentoCard
        {
            Id = Guid.NewGuid(),
            CompanyId = command.CompanyId,
            Title = command.Title.Trim(),
            Description = command.Description.Trim(),
            Column = "novo",
            AttachmentFileId = command.AttachmentFileId,
            CreatedByUserId = actor.UserId,
            UpdatedByUserId = actor.UserId,
            RequestId = command.RequestId,
            CreatedAt = now,
            UpdatedAt = now,
            RowVersion = 1,
        };
        foreach (var membershipId in command.ParticipantMembershipIds!)
        {
            card.Participants.Add(
                new AtendimentoCardParticipant
                {
                    CompanyId = command.CompanyId,
                    CardId = card.Id,
                    MembershipId = membershipId,
                }
            );
        }
        database.AtendimentoCards.Add(card);
        await database.SaveChangesAsync(cancellationToken);
        return ToResponse(card);
    }

    private async Task<AtendimentoCardResponse?> UpdateAssigneeAsync(
        ActorContext actor,
        Guid cardId,
        AssignAtendimentoCardCommand command,
        CancellationToken cancellationToken
    ) =>
        await UpdateCardAsync(
            cardId,
            command.CompanyId,
            command.ExpectedVersion,
            card =>
            {
                card.AssigneeMembershipId = command.AssigneeMembershipId;
                card.UpdatedByUserId = actor.UserId;
                card.UpdatedAt = DateTimeOffset.UtcNow;
                card.RequestId = command.RequestId;
            },
            cancellationToken
        );

    private async Task<AtendimentoCardResponse?> MoveCardAsync(
        ActorContext actor,
        Guid cardId,
        MoveAtendimentoCardCommand command,
        CancellationToken cancellationToken
    ) =>
        await UpdateCardAsync(
            cardId,
            command.CompanyId,
            command.ExpectedVersion,
            card =>
            {
                card.Column = command.Column.Trim();
                card.UpdatedByUserId = actor.UserId;
                card.UpdatedAt = DateTimeOffset.UtcNow;
                card.RequestId = command.RequestId;
            },
            cancellationToken
        );

    private async Task<AtendimentoCardResponse?> CancelCardAsync(
        ActorContext actor,
        Guid cardId,
        CancelAtendimentoCardCommand command,
        CancellationToken cancellationToken
    ) =>
        await UpdateCardAsync(
            cardId,
            command.CompanyId,
            command.ExpectedVersion,
            card =>
            {
                var now = DateTimeOffset.UtcNow;
                card.Column = "cancelado";
                card.CancelledAt = now;
                card.CancelledByUserId = actor.UserId;
                card.CancellationReason = command.Reason.Trim();
                card.UpdatedByUserId = actor.UserId;
                card.UpdatedAt = now;
                card.RequestId = command.RequestId;
            },
            cancellationToken
        );

    /// <summary>
    /// Atualização otimista: row_version é token de concorrência do EF; zero linhas afetadas
    /// devolve null e o chamador reconcilia (não encontrado x conflito de versão).
    /// </summary>
    private async Task<AtendimentoCardResponse?> UpdateCardAsync(
        Guid cardId,
        Guid companyId,
        long expectedVersion,
        Action<AtendimentoCard> apply,
        CancellationToken cancellationToken
    )
    {
        var card = await database
            .AtendimentoCards.Include(candidate => candidate.Participants)
            .SingleOrDefaultAsync(
                candidate => candidate.CompanyId == companyId && candidate.Id == cardId,
                cancellationToken
            );
        if (card is null || IsCancelled(card) || card.RowVersion != expectedVersion)
        {
            return null;
        }

        apply(card);
        card.RowVersion += 1;
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();
            return null;
        }

        return ToResponse(card);
    }

    private static AtendimentoCardResponse ToResponse(AtendimentoCard card) =>
        new(
            card.Id,
            card.CompanyId,
            card.Title,
            card.Description,
            card.Column,
            card.RowVersion,
            card.AssigneeMembershipId
        )
        {
            AttachmentFileId = card.AttachmentFileId,
            ParticipantMembershipIds =
            [
                .. card.Participants.OrderBy(
                    participant => participant.MembershipId.ToString("N"),
                    StringComparer.Ordinal
                ).Select(participant => participant.MembershipId),
            ],
        };

    private static bool IsIdempotencyKeyConflict(DbUpdateException exception)
    {
        for (
            Exception? inner = exception.InnerException;
            inner is not null;
            inner = inner.InnerException
        )
        {
            if (
                inner is PostgresException postgresException
                && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(
                    postgresException.ConstraintName,
                    IdempotencyRecordConfiguration.KeyIndexDatabaseName,
                    StringComparison.Ordinal
                )
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValid(CreateAtendimentoCardCommand command) =>
        command.Title?.Trim().Length is > 0 and <= 200
        && command.Description?.Trim().Length is > 0 and <= 4000
        && command.AttachmentFileId != Guid.Empty
        && command.ParticipantMembershipIds?.All(id => id != Guid.Empty) is not false;

    private static bool IsValid(AssignAtendimentoCardCommand command) =>
        command.ExpectedVersion > 0 && command.AssigneeMembershipId != Guid.Empty;

    private static bool IsValid(MoveAtendimentoCardCommand command) =>
        command.ExpectedVersion > 0
        && command.Column?.Trim() is "novo" or "em_andamento" or "concluido";

    private static bool IsValid(CancelAtendimentoCardCommand command) =>
        command.ExpectedVersion > 0 && command.Reason?.Trim().Length is > 0 and <= 500;

    private static CreateAtendimentoCardCommand NormalizeCreateCommand(
        CreateAtendimentoCardCommand command
    ) =>
        command with
        {
            ParticipantMembershipIds =
            [
                .. (command.ParticipantMembershipIds ?? [])
                    .Distinct()
                    .OrderBy(id => id.ToString("N"), StringComparer.Ordinal),
            ],
        };

    private static AtendimentoCardResponse NormalizeResponse(AtendimentoCardResponse response) =>
        response with { ParticipantMembershipIds = response.ParticipantMembershipIds ?? [] };

    private static bool IsCancelled(AtendimentoCardResponse card) =>
        string.Equals(card.Column, "cancelado", StringComparison.Ordinal);

    private static bool IsCancelled(AtendimentoCard card) =>
        string.Equals(card.Column, "cancelado", StringComparison.Ordinal);

    private static string CommandScope(string action, Guid cardId) => $"{action}:{cardId:N}";

    private static string HashCreate(CreateAtendimentoCardCommand command) =>
        command.AttachmentFileId is null && command.ParticipantMembershipIds!.Count == 0
            ? Hash(
                new LegacyCreateAtendimentoCardPayload(
                    command.CompanyId,
                    command.RequestId,
                    command.Title,
                    command.Description
                )
            )
            : Hash(command);

    private static string Hash<T>(T value) =>
        Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))))
            .ToLowerInvariant();

    private static string ErrorCode(AtendimentoCardOperationState state) =>
        state switch
        {
            AtendimentoCardOperationState.MembershipRequired => "membership_required",
            AtendimentoCardOperationState.CapabilityDenied => "capability_denied",
            AtendimentoCardOperationState.ResourceNotFound => "resource_not_found",
            AtendimentoCardOperationState.IdempotencyConflict => "idempotency_conflict",
            AtendimentoCardOperationState.IntegrityConflict => "integrity_conflict",
            AtendimentoCardOperationState.TerminalState => "terminal_state",
            _ => "atendimento_unavailable",
        };

    private void LogInformation(string result, string reason) =>
#pragma warning disable CA1873 // Os argumentos são constantes ou valores baratos; o guard de logging não agrega ganho.
        logger.LogInformation(
            "Atendimento {Operation} {Module} {Result} {Reason}",
            Operation,
            Module,
            result,
            reason
        );
#pragma warning restore CA1873

    private void LogUnavailable(Exception? exception, string reason) =>
        logger.LogError(
            exception,
            "Atendimento {Operation} {Module} {Result} {Reason}",
            Operation,
            Module,
            "unavailable",
            reason
        );

    private sealed record RoutedCommandPayload<T>(Guid CardId, T Command);

    private sealed record LegacyCreateAtendimentoCardPayload(
        Guid CompanyId,
        Guid RequestId,
        string Title,
        string Description
    );

    private sealed record Mutation(
        AtendimentoCardOperationState State,
        AtendimentoCardResponse? Card = null,
        AtendimentoCardResponse? Current = null
    )
    {
        public static Mutation Succeeded(
            AtendimentoCardResponse card,
            AtendimentoCardOperationState state
        ) => new(state, card);

        public static Mutation ResourceNotFound() =>
            new(AtendimentoCardOperationState.ResourceNotFound);

        public static Mutation IntegrityConflict(AtendimentoCardResponse current) =>
            new(AtendimentoCardOperationState.IntegrityConflict, Current: current);

        public static Mutation Terminal(AtendimentoCardResponse current) =>
            new(AtendimentoCardOperationState.TerminalState, Current: current);
    }
}
