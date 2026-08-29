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
using Vinx.Erp.Foundation.Notifications;
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
    NotificationIntentService notificationIntents,
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
    private const string FieldsAction = "service_desk.ticket.fields";
    private const string WatchersAction = "service_desk.ticket.watchers";
    private const string ReadTimelineAction = "service_desk.ticket.read_timeline";
    private const string CategoryCreateAction = "service_desk.lookup.create_category";
    private const string CategoryDeactivateAction = "service_desk.lookup.deactivate_category";

    /// <summary>Templates in-app aprovados por migração; chaves também ancoram os intents.</summary>
    private const string AssignedTemplateKey = "atendimento.card.assigned";
    private const string MentionedTemplateKey = "atendimento.card.mentioned";
    private const string WatcherAddedTemplateKey = "atendimento.card.watcher_added";

    private const string ClosedColumn = "concluido";
    private const string CancelledColumn = "cancelado";

    private static readonly HashSet<string> ValidPriorities =
        new(StringComparer.Ordinal) { "critica", "alta", "media", "baixa" };

    private static readonly HashSet<string> ValidColumns =
        new(StringComparer.Ordinal) { "novo", "em_andamento", ClosedColumn, CancelledColumn };

    /// <summary>
    /// Intents saem APÓS o commit da mutação: NotificationIntentService abre a
    /// própria transação no mesmo DbContext, logo não compõe com a transação do
    /// produtor. Falha é best-effort (UX de aviso) com chave determinística para
    /// dedupe downstream; o estado de negócio nunca depende dela.
    /// </summary>
    private sealed record PendingIntent(
        Guid CompanyId,
        Guid CardId,
        string TemplateKey,
        string Kind,
        Guid RecipientUserId
    );

    private const string AssignedKind = "assigned";
    private const string MentionedKind = "mentioned";
    private const string WatcherAddedKind = "watcher_added";

    public async Task<AtendimentoCardOperationResult> CreateAsync(
        ActorContext actor,
        CreateAtendimentoCardCommand command,
        CancellationToken cancellationToken
    )
    {
        var normalized = NormalizeCreateCommand(command);
        var now = DateTimeOffset.UtcNow;
        var pending = new List<PendingIntent>();
        var result = await ExecuteAsync(
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

                var card = await InsertCardAsync(actor, normalized, now, cancellationToken);
                return Mutation.Succeeded(card, AtendimentoCardOperationState.Created);
            },
            now, cancellationToken
        );
        await FlushOnTerminalSuccessAsync(actor, result.State, pending, cancellationToken);
        return result;
    }

    public async Task<AtendimentoCardOperationResult> AssignAsync(
        ActorContext actor,
        Guid cardId,
        AssignAtendimentoCardCommand command,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        var pending = new List<PendingIntent>();
        var result = await ExecuteAsync(
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
                var before = await FindCardAsync(command.CompanyId, cardId, now, cancellationToken);
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

                var updated = await UpdateAssigneeAsync(actor, cardId, command, now, cancellationToken);
                if (updated is not null)
                {
                    var actorMembershipId = await ResolveActorMembershipIdAsync(
                        actor,
                        command.CompanyId,
                        cancellationToken
                    );
                    await AppendCardEventAsync(
                        command.CompanyId,
                        cardId,
                        actorMembershipId,
                        AssignedKind,
                        new { assigneeMembershipId = command.AssigneeMembershipId },
                        cancellationToken
                    );
                    var recipientUserId = await ResolveMembershipUserIdAsync(
                        command.CompanyId,
                        command.AssigneeMembershipId,
                        cancellationToken
                    );
                    if (recipientUserId is not null)
                    {
                        pending.Add(
                            new PendingIntent(
                                command.CompanyId,
                                cardId,
                                AssignedTemplateKey,
                                AssignedKind,
                                recipientUserId.Value
                            )
                        );
                    }
                    return Mutation.Succeeded(updated, AtendimentoCardOperationState.Success);
                }

                return await ReadCurrentAfterMutationMissAsync(
                    command.CompanyId, cardId, now,
                    cancellationToken
                );
            },
            now, cancellationToken
        );
        await FlushOnTerminalSuccessAsync(actor, result.State, pending, cancellationToken);
        return result;
    }

    public Task<AtendimentoCardOperationResult> MoveAsync(
        ActorContext actor,
        Guid cardId,
        MoveAtendimentoCardCommand command,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        return ExecuteAsync(
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
                var before = await FindCardAsync(command.CompanyId, cardId, now, cancellationToken);
                if (before is null)
                {
                    return Mutation.ResourceNotFound();
                }

                if (IsCancelled(before))
                {
                    return Mutation.IntegrityConflict(before);
                }

                var updated = await MoveCardAsync(actor, before.Id, command, now, cancellationToken);
                if (updated is null)
                {
                    return await ReadCurrentAfterMutationMissAsync(
                        command.CompanyId, cardId, now,
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
                var actorMembershipId = await ResolveActorMembershipIdAsync(
                    actor,
                    command.CompanyId,
                    cancellationToken
                );
                await AppendCardEventAsync(
                    command.CompanyId,
                    cardId,
                    actorMembershipId,
                    "moved",
                    new { from = before.Column, to = command.Column.Trim() },
                    cancellationToken
                );
                return Mutation.Succeeded(updated, AtendimentoCardOperationState.Success);
            },
            now, cancellationToken
        );
    }

    public Task<AtendimentoCardOperationResult> CancelAsync(
        ActorContext actor,
        Guid cardId,
        CancelAtendimentoCardCommand command,
        CancellationToken cancellationToken
    )
    {
        var normalized = command with { Reason = command.Reason?.Trim() ?? string.Empty };
        var now = DateTimeOffset.UtcNow;
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
                var before = await FindCardAsync(normalized.CompanyId, cardId, now, cancellationToken);
                if (before is null)
                {
                    return Mutation.ResourceNotFound();
                }

                if (IsCancelled(before))
                {
                    return Mutation.Terminal(before);
                }

                var updated = await CancelCardAsync(actor, cardId, normalized, now, cancellationToken);
                if (updated is null)
                {
                    return await ReadCurrentAfterMutationMissAsync(
                        normalized.CompanyId, cardId, now,
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
            now, cancellationToken
        );
    }

    public async Task<AtendimentoCardOperationResult> GetQueueAsync(
        ActorContext actor,
        Guid companyId,
        AtendimentoCardQueueFilter? filter,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        if (companyId == Guid.Empty)
        {
            return new(AtendimentoCardOperationState.Invalid);
        }

        if (filter?.Status is { } status && !ValidColumns.Contains(status))
        {
            return new(AtendimentoCardOperationState.Invalid);
        }
        if (filter?.Priority is { } priority && !ValidPriorities.Contains(priority))
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

            var cards = await QueryCardsAsync(companyId, filter, now, cancellationToken);
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

    /// <summary>Atualiza os campos de ticket com semântica null = inalterado.</summary>
    public async Task<AtendimentoCardOperationResult> UpdateFieldsAsync(
        ActorContext actor,
        Guid cardId,
        UpdateAtendimentoCardFieldsCommand command,
        CancellationToken cancellationToken
    )
    {
        var pending = new List<PendingIntent>();
        var now = DateTimeOffset.UtcNow;
        var result = await ExecuteAsync(
            actor,
            command.CompanyId,
            command.RequestId,
            KnownCapabilities.ServiceDeskTicketFields,
            FieldsAction,
            CommandScope(FieldsAction, cardId),
            IsValid(command),
            Hash(new RoutedCommandPayload<UpdateAtendimentoCardFieldsCommand>(cardId, command)),
            () => UpdateTicketingFieldsAsync(actor, cardId, command, pending, cancellationToken),
            now, cancellationToken
        );
        await FlushOnTerminalSuccessAsync(actor, result.State, pending, cancellationToken);
        return result;
    }

    private async Task<Mutation> UpdateTicketingFieldsAsync(
        ActorContext actor,
        Guid cardId,
        UpdateAtendimentoCardFieldsCommand command,
        List<PendingIntent> pending,
        CancellationToken cancellationToken
    )
    {
        // Relógio-dependente e avaliado DEPOIS da resolução de replay: um
        // comando legítimo antigo continua reexecutável como Replay.
        if (command.SlaDueAt is { } pendingSla && pendingSla <= DateTimeOffset.UtcNow)
        {
            return Mutation.Invalid();
        }

        var card = await database.AtendimentoCards.SingleOrDefaultAsync(
            candidate => candidate.CompanyId == command.CompanyId && candidate.Id == cardId,
            cancellationToken
        );
        if (card is null)
        {
            return Mutation.ResourceNotFound();
        }
        if (IsCancelled(card))
        {
            // Cancelado é terminal (mesmo contrato do caminho de mutação
            // compartilhado): campos nunca mudam após o cancelamento.
            var cancelled = await FindCardAsync(
                command.CompanyId,
                cardId,
                DateTimeOffset.UtcNow,
                cancellationToken
            );
            return cancelled is null
                ? Mutation.ResourceNotFound()
                : Mutation.IntegrityConflict(cancelled);
        }
        if (card.RowVersion != command.ExpectedVersion)
        {
            return await ReadCurrentAfterMutationMissAsync(
                command.CompanyId,
                cardId,
                DateTimeOffset.UtcNow,
                cancellationToken
            );
        }

        if (
            command.CategoryRefId is { } categoryId
            && !await database.AtendimentoCardCategories.AnyAsync(
                category =>
                    category.CompanyId == command.CompanyId && category.Id == categoryId,
                cancellationToken
            )
        )
        {
            return Mutation.ResourceNotFound();
        }
        if (
            command.ChannelRefId is { } channelId
            && !await database.AtendimentoChannels.AnyAsync(
                channel =>
                    channel.CompanyId == command.CompanyId && channel.Id == channelId,
                cancellationToken
            )
        )
        {
            return Mutation.ResourceNotFound();
        }

        var mentionedMembershipIds = command.MentionedMembershipIds?
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (
            mentionedMembershipIds is { Length: > 0 }
            && await CountActiveMembershipsAsync(
                command.CompanyId,
                mentionedMembershipIds,
                cancellationToken
            ) != mentionedMembershipIds.Length
        )
        {
            // Menção de membership inexistente/inativa é referência desconhecida.
            return Mutation.ResourceNotFound();
        }

        var changed = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (command.Priority is { } priority)
        {
            card.Priority = priority.Trim();
            changed[nameof(card.Priority)] = card.Priority;
        }
        if (command.CategoryRefId is { } categoryRef)
        {
            card.CategoryRefId = categoryRef;
            changed[nameof(card.CategoryRefId)] = categoryRef;
        }
        if (command.ChannelRefId is { } channelRef)
        {
            card.ChannelRefId = channelRef;
            changed[nameof(card.ChannelRefId)] = channelRef;
        }
        if (command.SlaDueAt is { } slaDueAt)
        {
            card.SlaDueAt = slaDueAt;
            changed[nameof(card.SlaDueAt)] = slaDueAt;
        }
        if (command.RequesterPersonRef is { } requester && !string.IsNullOrWhiteSpace(requester))
        {
            card.RequesterPersonRef = requester.Trim();
            changed[nameof(card.RequesterPersonRef)] = card.RequesterPersonRef;
        }

        card.RowVersion += 1;
        card.UpdatedByUserId = actor.UserId;
        card.UpdatedAt = DateTimeOffset.UtcNow;
        card.RequestId = command.RequestId;
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();
            return await ReadCurrentAfterMutationMissAsync(
                command.CompanyId,
                cardId,
                DateTimeOffset.UtcNow,
                cancellationToken
            );
        }

        var actorMembershipId = await ResolveActorMembershipIdAsync(
            actor,
            command.CompanyId,
            cancellationToken
        );
        if (mentionedMembershipIds is { Length: > 0 })
        {
            // Menção tem evento próprio, além do fields_updated dos demais campos.
            await AppendCardEventAsync(
                command.CompanyId,
                cardId,
                actorMembershipId,
                MentionedKind,
                new { mentionedMembershipIds },
                cancellationToken
            );
        }

        await AppendCardEventAsync(
            command.CompanyId,
            cardId,
            actorMembershipId,
            "fields_updated",
            new
            {
                fields = changed.Keys.ToArray(),
                mentionedMembershipIds = mentionedMembershipIds ?? Array.Empty<Guid>(),
            },
            cancellationToken
        );

        if (mentionedMembershipIds is { Length: > 0 })
        {
            foreach (var mentioned in await ResolveMembershipUserIdsAsync(
                command.CompanyId,
                mentionedMembershipIds,
                cancellationToken
            ))
            {
                pending.Add(
                    new PendingIntent(
                        command.CompanyId,
                        cardId,
                        MentionedTemplateKey,
                        MentionedKind,
                        mentioned
                    )
                );
            }
        }

        return Mutation.Succeeded(
            await FindTrackedCardAsync(command.CompanyId, cardId, cancellationToken),
            AtendimentoCardOperationState.Success
        );
    }

    private async Task<AtendimentoCardResponse> FindTrackedCardAsync(
        Guid companyId,
        Guid cardId,
        CancellationToken cancellationToken
    )
    {
        var card = await database
            .AtendimentoCards.AsNoTracking()
            .Include(card => card.Participants)
            .SingleAsync(
                candidate => candidate.CompanyId == companyId && candidate.Id == cardId,
                cancellationToken
            );
        var watchers = await LoadWatcherMembershipIdsAsync(companyId, [cardId], cancellationToken);
        return ToResponse(card, DateTimeOffset.UtcNow, watchers.GetValueOrDefault(cardId));
    }

    public async Task<AtendimentoCardOperationResult> AddWatcherAsync(
        ActorContext actor,
        Guid cardId,
        AtendimentoCardWatcherCommand command,
        CancellationToken cancellationToken
    )
    {
        var pending = new List<PendingIntent>();
        var result = await ExecuteWatcherAsync(
            actor,
            cardId,
            command,
            isAdd: true,
            pending,
            cancellationToken
        );
        await FlushOnTerminalSuccessAsync(actor, result.State, pending, cancellationToken);
        return result;
    }

    public async Task<AtendimentoCardOperationResult> RemoveWatcherAsync(
        ActorContext actor,
        Guid cardId,
        AtendimentoCardWatcherCommand command,
        CancellationToken cancellationToken
    )
    {
        var pending = new List<PendingIntent>();
        var result = await ExecuteWatcherAsync(
            actor,
            cardId,
            command,
            isAdd: false,
            pending,
            cancellationToken
        );
        await FlushOnTerminalSuccessAsync(actor, result.State, pending, cancellationToken);
        return result;
    }

    private Task<AtendimentoCardOperationResult> ExecuteWatcherAsync(
        ActorContext actor,
        Guid cardId,
        AtendimentoCardWatcherCommand command,
        bool isAdd,
        List<PendingIntent> pending,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        return ExecuteAsync(
            actor,
            command.CompanyId,
            command.RequestId,
            capability: KnownCapabilities.ServiceDeskTicketWatchers,
            auditAction: WatchersAction,
            idempotencyCommandName: CommandScope(
                $"{WatchersAction}.{(isAdd ? "add" : "remove")}",
                cardId
            ),
            valid: IsValid(command),
            payloadHash: Hash(new WatcherCommandPayload(cardId, command, isAdd)),
            async () =>
            {
                var presenceChecked = await IsWatcherPresentAsync(
                    command.CompanyId,
                    cardId,
                    command.MemberMembershipId,
                    cancellationToken
                );

                AtendimentoCardResponse? before;
                if (isAdd)
                {
                    if (
                        !await IsActiveMembershipAsync(
                            command.CompanyId,
                            command.MemberMembershipId,
                            cancellationToken
                        )
                    )
                    {
                        return Mutation.ResourceNotFound();
                    }

                    before = await FindCardAsync(command.CompanyId, cardId, now, cancellationToken);
                    if (before is null)
                    {
                        return Mutation.ResourceNotFound();
                    }

                    if (presenceChecked)
                    {
                        if (before.Version != command.ExpectedVersion)
                        {
                            // Repetição com versão obsoleta: conflito com o estado atual.
                            return Mutation.IntegrityConflict(before);
                        }

                        // Repetição de adição com versão corrente: nada acontece e a versão fica.
                        return Mutation.Succeeded(before, AtendimentoCardOperationState.NoChange);
                    }

                    // Bump otimista ANTES do DML; divergência devolve estado atual.
                    var bumpedAdd = await UpdateCardAsync(
                        cardId,
                        command.CompanyId,
                        command.ExpectedVersion,
                        card =>
                        {
                            card.RequestId = command.RequestId;
                        },
                        now,
                        cancellationToken
                    );
                    if (bumpedAdd is null)
                    {
                        return await ReadCurrentAfterMutationMissAsync(
                            command.CompanyId,
                            cardId,
                            now,
                            cancellationToken
                        );
                    }

                    var insertedRows = await database.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO service_desk.atendimento_card_watchers
                            (id, company_id, card_id, membership_id, created_by_user_id, request_id)
                        VALUES ({Guid.NewGuid()}, {command.CompanyId}, {cardId}, {command.MemberMembershipId}, {actor.UserId}, {command.RequestId})
                        ON CONFLICT (company_id, card_id, membership_id) DO NOTHING
                        """,
                        cancellationToken
                    );
                    if (insertedRows == 0)
                    {
                        // Corrida com gêmea vencedora: repete como NoChange.
                        var raced = await FindCardAsync(
                            command.CompanyId,
                            cardId,
                            now,
                            cancellationToken
                        );
                        return raced is null
                            ? Mutation.ResourceNotFound()
                            : Mutation.Succeeded(raced, AtendimentoCardOperationState.NoChange);
                    }
                    var addActor = await ResolveActorMembershipIdAsync(
                        actor,
                        command.CompanyId,
                        cancellationToken
                    );
                    await AppendCardEventAsync(
                        command.CompanyId,
                        cardId,
                        addActor,
                        "watcher_added",
                        new { watcherMembershipId = command.MemberMembershipId },
                        cancellationToken
                    );
                    var recipientUserId = await ResolveMembershipUserIdAsync(
                        command.CompanyId,
                        command.MemberMembershipId,
                        cancellationToken
                    );
                    if (recipientUserId is not null)
                    {
                        pending.Add(
                            new PendingIntent(
                                command.CompanyId,
                                cardId,
                                WatcherAddedTemplateKey,
                                WatcherAddedKind,
                                recipientUserId.Value
                            )
                        );
                    }
                }
                else
                {
                    before = await FindCardAsync(command.CompanyId, cardId, now, cancellationToken);
                    if (before is null)
                    {
                        return Mutation.ResourceNotFound();
                    }

                    if (!presenceChecked)
                    {
                        if (before.Version != command.ExpectedVersion)
                        {
                            // Ausência com versão obsoleta: conflito com o estado atual.
                            return Mutation.IntegrityConflict(before);
                        }

                        // Remoção de vínculo inexistente com versão corrente: idempotente, versão fica.
                        return Mutation.Succeeded(before, AtendimentoCardOperationState.NoChange);
                    }

                    // Bump otimista ANTES do DELETE, mesma disciplina da adição.
                    var bumpedRemove = await UpdateCardAsync(
                        cardId,
                        command.CompanyId,
                        command.ExpectedVersion,
                        card =>
                        {
                            card.RequestId = command.RequestId;
                        },
                        now,
                        cancellationToken
                    );
                    if (bumpedRemove is null)
                    {
                        return await ReadCurrentAfterMutationMissAsync(
                            command.CompanyId,
                            cardId,
                            now,
                            cancellationToken
                        );
                    }

                    await database.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        DELETE FROM service_desk.atendimento_card_watchers
                        WHERE company_id = {command.CompanyId}
                            AND card_id = {cardId}
                            AND membership_id = {command.MemberMembershipId}
                        """,
                        cancellationToken
                    );
                    var removeActor = await ResolveActorMembershipIdAsync(
                        actor,
                        command.CompanyId,
                        cancellationToken
                    );
                    await AppendCardEventAsync(
                        command.CompanyId,
                        cardId,
                        removeActor,
                        "watcher_removed",
                        new { watcherMembershipId = command.MemberMembershipId },
                        cancellationToken
                    );
                }

                var current = await FindCardAsync(command.CompanyId, cardId, now, cancellationToken);
                return current is null
                    ? Mutation.ResourceNotFound()
                    : Mutation.Succeeded(current, AtendimentoCardOperationState.Success);
            },
            now, cancellationToken
        );
    }

    /// <summary>
    /// Timeline do cartão: últimos 100 eventos em ordem crescente com nome do
    /// ator resolvido no servidor via membership → usuário.
    /// </summary>
    public async Task<AtendimentoTimelineResult> GetTimelineAsync(
        ActorContext actor,
        Guid companyId,
        Guid cardId,
        CancellationToken cancellationToken
    )
    {
        if (companyId == Guid.Empty || cardId == Guid.Empty)
        {
            return new(AtendimentoTimelineReadState.Invalid);
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
                return denied is AtendimentoCardOperationState.MembershipRequired
                    ? new(AtendimentoTimelineReadState.MembershipRequired)
                    : new(AtendimentoTimelineReadState.CapabilityDenied);
            }

            var exists = await database.AtendimentoCards.AsNoTracking().AnyAsync(
                card => card.CompanyId == companyId && card.Id == cardId,
                cancellationToken
            );
            if (!exists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(AtendimentoTimelineReadState.ResourceNotFound);
            }

            var entries = (await database.Database.SqlQuery<TimelineRow>(
                $"""
                SELECT entry.id,
                       entry.event_seq,
                       entry.action_kind,
                       entry.actor_membership_id,
                       COALESCE(resolved.display_name, '') AS actor_display_name,
                       entry.occurred_at
                FROM (
                    SELECT *
                    FROM service_desk.atendimento_card_events
                    WHERE company_id = {companyId} AND card_id = {cardId}
                    ORDER BY event_seq DESC
                    LIMIT 100
                ) AS entry
                LEFT JOIN service_desk.resolve_card_actor_names({companyId}, {cardId}) AS resolved
                    ON resolved.actor_membership_id = entry.actor_membership_id
                ORDER BY entry.event_seq ASC
                """
            ).ToListAsync(cancellationToken))
                .Select(row => new AtendimentoTimelineEntry(
                    row.Id,
                    row.EventSeq,
                    row.ActionKind,
                    row.ActorMembershipId,
                    string.IsNullOrWhiteSpace(row.ActorDisplayName) ? "(sem ator)" : row.ActorDisplayName,
                    row.OccurredAt
                ))
                .ToList();

            await AppendAuditAsync(
                actor,
                companyId,
                ReadTimelineAction,
                cardId,
                AuditOutcome.Success,
                null,
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            return new(AtendimentoTimelineReadState.Success, entries);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "timeline_read_failed");
            return new(AtendimentoTimelineReadState.Unavailable);
        }
    }

    private sealed record TimelineRow(
        Guid Id,
        long EventSeq,
        string ActionKind,
        Guid? ActorMembershipId,
        string ActorDisplayName,
        DateTimeOffset OccurredAt
    );

    public async Task<AtendimentoLookupsResult> GetLookupsAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    )
    {
        if (companyId == Guid.Empty)
        {
            return new(AtendimentoLookupsReadState.Invalid);
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
                return denied is AtendimentoCardOperationState.MembershipRequired
                    ? new(AtendimentoLookupsReadState.MembershipRequired)
                    : new(AtendimentoLookupsReadState.CapabilityDenied);
            }

            var categories = await database
                .AtendimentoCardCategories.AsNoTracking()
                .Where(category => category.CompanyId == companyId)
                .OrderBy(category => category.Name)
                .Select(category => new AtendimentoLookupCategoryItem(
                    category.Id,
                    category.Name,
                    category.IsActive
                ))
                .ToListAsync(cancellationToken);
            var channels = await database
                .AtendimentoChannels.AsNoTracking()
                .Where(channel => channel.CompanyId == companyId)
                .OrderBy(channel => channel.Key)
                .Select(channel => new AtendimentoLookupChannelItem(
                    channel.Id,
                    channel.Key,
                    channel.IsActive
                ))
                .ToListAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new(
                AtendimentoLookupsReadState.Success,
                new AtendimentoLookupsResponse(categories, channels)
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "lookups_read_failed");
            return new(AtendimentoLookupsReadState.Unavailable);
        }
    }

    /// <summary>
    /// Cria categoria por empresa; nome é aparado e duplicata é conflito
    /// estável. Passa pelo mesmo executor de idempotência com comparação de
    /// hash e reconciliação de concorrência das mutações de cartão.
    /// </summary>
    public Task<AtendimentoLookupWriteResult> CreateCategoryAsync(
        ActorContext actor,
        CreateAtendimentoCategoryCommand command,
        CancellationToken cancellationToken
    )
    {
        var name = command.Name?.Trim();
        return ExecuteLookupWriteAsync(
            actor,
            command.CompanyId,
            command.RequestId,
            CategoryCreateAction,
            CategoryCreateAction,
            valid: !string.IsNullOrWhiteSpace(name) && name.Length <= 80,
            payloadHash: Hash(name),
            async () =>
            {
                var duplicate = await database.AtendimentoCardCategories.AnyAsync(
                    category =>
                        category.CompanyId == command.CompanyId && category.Name == name,
                    cancellationToken
                );
                if (duplicate)
                {
                    return LookupMutation.Conflict();
                }

                var now = DateTimeOffset.UtcNow;
                var category = new AtendimentoCardCategory
                {
                    Id = Guid.NewGuid(),
                    CompanyId = command.CompanyId,
                    Name = name!,
                    IsActive = true,
                    CreatedByUserId = actor.UserId,
                    UpdatedByUserId = actor.UserId,
                    RequestId = command.RequestId,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                database.AtendimentoCardCategories.Add(category);
                await database.SaveChangesAsync(cancellationToken);
                return LookupMutation.Succeeded(
                    new AtendimentoCategoryResponse(category.Id, category.CompanyId, name!),
                    AtendimentoLookupWriteState.Created
                );
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Desativa categoria com recibo de idempotência: 200 Success/Replay para
    /// o comando e o retry; reuso divergente do mesmo requestId é conflito.
    /// </summary>
    public Task<AtendimentoLookupWriteResult> DeactivateCategoryAsync(
        ActorContext actor,
        DeactivateAtendimentoCategoryCommand command,
        CancellationToken cancellationToken
    ) =>
        ExecuteLookupWriteAsync(
            actor,
            command.CompanyId,
            command.RequestId,
            CategoryDeactivateAction,
            CommandScope(CategoryDeactivateAction, command.CategoryId),
            valid: command.CategoryId != Guid.Empty,
            payloadHash: Hash(new DeactivateCategoryPayload(command.CategoryId)),
            async () =>
            {
                var category = await database.AtendimentoCardCategories.SingleOrDefaultAsync(
                    candidate =>
                        candidate.CompanyId == command.CompanyId
                        && candidate.Id == command.CategoryId,
                    cancellationToken
                );
                if (category is null)
                {
                    return LookupMutation.ResourceNotFound();
                }

                if (category.IsActive)
                {
                    category.IsActive = false;
                    category.UpdatedByUserId = actor.UserId;
                    category.UpdatedAt = DateTimeOffset.UtcNow;
                    category.RequestId = command.RequestId;
                    await database.SaveChangesAsync(cancellationToken);
                }
                return LookupMutation.Succeeded(
                    new AtendimentoCategoryResponse(category.Id, category.CompanyId, category.Name),
                    AtendimentoLookupWriteState.Success
                );
            },
            cancellationToken
        );

    /// <summary>
    /// Executor de escrita de lookups no padrão de ExecuteAsync: valida,
    /// autoriza, compara hash do recibo de idempotência (divergente é
    /// idempotency_conflict), persiste recibo+auditoria na mesma transação e
    /// reconcilia corrida de chave única como Replay/Conflict.
    /// </summary>
    private async Task<AtendimentoLookupWriteResult> ExecuteLookupWriteAsync(
        ActorContext actor,
        Guid companyId,
        Guid requestId,
        string auditAction,
        string idempotencyCommandName,
        bool valid,
        string payloadHash,
        Func<Task<LookupMutation>> mutation,
        CancellationToken cancellationToken
    )
    {
        if (companyId == Guid.Empty || requestId == Guid.Empty || !valid)
        {
            return new(AtendimentoLookupWriteState.Invalid);
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
                KnownCapabilities.ServiceDeskLookupAdmin,
                cancellationToken
            );
            if (denied is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await AuditLookupDeniedAsync(
                    actor,
                    companyId,
                    auditAction,
                    denied is AtendimentoCardOperationState.MembershipRequired
                        ? AtendimentoLookupWriteState.MembershipRequired
                        : AtendimentoLookupWriteState.CapabilityDenied,
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
                    return await AuditLookupDeniedAsync(
                        actor,
                        companyId,
                        auditAction,
                        AtendimentoLookupWriteState.IdempotencyConflict,
                        cancellationToken
                    );
                }

                var replay = JsonSerializer.Deserialize<AtendimentoCategoryResponse>(
                    existing.ResultJson
                );
                if (replay is null)
                {
                    LogUnavailable(null, "idempotency_result_invalid");
                    return new(AtendimentoLookupWriteState.Unavailable);
                }
                LogInformation("replayed", "idempotency_match");
                return new(AtendimentoLookupWriteState.Replay, replay);
            }

            var result = await mutation();
            if (
                result.State
                is AtendimentoLookupWriteState.ResourceNotFound
                    or AtendimentoLookupWriteState.Conflict
            )
            {
                await transaction.RollbackAsync(cancellationToken);
                return await AuditLookupDeniedAsync(
                    actor,
                    companyId,
                    auditAction,
                    result.State,
                    cancellationToken
                );
            }

            database.IdempotencyRecords.Add(
                new IdempotencyRecord(
                    Guid.NewGuid(),
                    companyId,
                    actor.UserId,
                    idempotencyCommandName,
                    requestId,
                    payloadHash,
                    JsonSerializer.Serialize(result.Category)
                )
            );
            await AppendAuditAsync(
                actor,
                companyId,
                auditAction,
                result.Category!.Id,
                AuditOutcome.Success,
                null,
                cancellationToken
            );
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogInformation("succeeded", "lookup_command_completed");
            return new(result.State, result.Category);
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
            return await ResolveConcurrentLookupReplayAsync(
                actor,
                companyId,
                requestId,
                auditAction,
                idempotencyCommandName,
                payloadHash,
                cancellationToken
            );
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres
            && postgres.ConstraintName == "uq_atendimento_categories_company_id_name")
        {
            // Corrida de criação concorrente: mesmo request/hash reconcilia como
            // Replay; só um request divergente sob o mesmo nome é conflito.
            database.ChangeTracker.Clear();
            return await ResolveConcurrentLookupReplayAsync(
                actor,
                companyId,
                requestId,
                auditAction,
                idempotencyCommandName,
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
            LogUnavailable(exception, "lookup_write_failed");
            return new(AtendimentoLookupWriteState.Unavailable);
        }
    }

    private async Task<AtendimentoLookupWriteResult> ResolveConcurrentLookupReplayAsync(
        ActorContext actor,
        Guid companyId,
        Guid requestId,
        string auditAction,
        string idempotencyCommandName,
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
                denial = await AuthorizeAsync(
                    actor,
                    companyId,
                    KnownCapabilities.ServiceDeskLookupAdmin,
                    cancellationToken
                );
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
                return await AuditLookupDeniedAsync(
                    actor,
                    companyId,
                    auditAction,
                    denial is AtendimentoCardOperationState.MembershipRequired
                        ? AtendimentoLookupWriteState.MembershipRequired
                        : AtendimentoLookupWriteState.CapabilityDenied,
                    cancellationToken
                );
            }

            if (record is null)
            {
                return new(AtendimentoLookupWriteState.Conflict);
            }

            if (!string.Equals(record.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return await AuditLookupDeniedAsync(
                    actor,
                    companyId,
                    auditAction,
                    AtendimentoLookupWriteState.IdempotencyConflict,
                    cancellationToken
                );
            }

            var replay = JsonSerializer.Deserialize<AtendimentoCategoryResponse>(record.ResultJson);
            if (replay is null)
            {
                LogUnavailable(null, "idempotency_result_invalid");
                return new(AtendimentoLookupWriteState.Unavailable);
            }

            LogInformation("replayed", "concurrent_idempotency_match");
            return new(AtendimentoLookupWriteState.Replay, replay);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "idempotency_reconciliation_failed");
            return new(AtendimentoLookupWriteState.Unavailable);
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
        DateTimeOffset now,
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
                return new(
                    AtendimentoCardOperationState.Replay,
                    NormalizeResponse(replay, now)
                );
            }

            var result = await mutation();
            if (
                result.State
                    is AtendimentoCardOperationState.ResourceNotFound
                    or AtendimentoCardOperationState.Invalid
            )
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
                payloadHash, now,
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
        DateTimeOffset now,
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
            return new(
                AtendimentoCardOperationState.Replay,
                NormalizeResponse(replay, now)
            );
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
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var current = await FindCardAsync(companyId, cardId, now, cancellationToken);
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
                actor.CorrelationId,
                reason,
                "{}"
            ),
            cancellationToken
        );

    private async Task<List<AtendimentoCardResponse>> QueryCardsAsync(
        Guid companyId,
        AtendimentoCardQueueFilter? filter,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var query = database
            .AtendimentoCards.AsNoTracking()
            .Include(card => card.Participants)
            .Where(card => card.CompanyId == companyId);
        if (filter is not null)
        {
            if (filter.Status is { } status)
            {
                query = query.Where(card => card.Column == status);
            }
            if (filter.Priority is { } priority)
            {
                query = query.Where(card => card.Priority == priority);
            }
            if (filter.AssigneeMembershipId is { } assignee)
            {
                query = query.Where(card => card.AssigneeMembershipId == assignee);
            }
            if (filter.Overdue)
            {
                // Mesma predica terminal de IsOverdue: concluído e cancelado
                // nunca aparecem como atrasados.
                query = query.Where(
                    card =>
                        card.SlaDueAt != null
                        && card.SlaDueAt < now
                        && card.Column != ClosedColumn
                        && card.Column != CancelledColumn
                );
            }
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var pattern = $"%{EscapeLike(filter.Search.Trim())}%";
                query = query.Where(
                    card =>
                        EF.Functions.ILike(card.Title, pattern)
                        || EF.Functions.ILike(card.Description, pattern)
                );
            }
        }

        var cards = await query
            .OrderByDescending(card => card.CreatedAt)
            .ThenByDescending(card => card.Id)
            .ToListAsync(cancellationToken);
        var watchers = await LoadWatcherMembershipIdsAsync(
            companyId,
            [.. cards.Select(card => card.Id)],
            cancellationToken
        );
        return [.. cards.Select(card => ToResponse(card, now, watchers.GetValueOrDefault(card.Id)))];
    }

    /// <summary>Máscara LIKE/ILIKE: barra invertida e curingas escapados.</summary>
    private static string EscapeLike(string value) =>
        value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private async Task<Dictionary<Guid, List<Guid>>> LoadWatcherMembershipIdsAsync(
        Guid companyId,
        IReadOnlyList<Guid> cardIds,
        CancellationToken cancellationToken
    )
    {
        if (cardIds.Count == 0)
        {
            return [];
        }

        var rows = await database
            .AtendimentoCardWatchers.AsNoTracking()
            .Where(watcher => watcher.CompanyId == companyId && cardIds.Contains(watcher.CardId))
            .OrderBy(watcher => watcher.CreatedAt)
            .Select(watcher => new { watcher.CardId, watcher.MembershipId })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(row => row.CardId, row => row.MembershipId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    private async Task<AtendimentoCardResponse?> FindCardAsync(
        Guid companyId,
        Guid cardId,
        DateTimeOffset now,
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
        if (card is null)
        {
            return null;
        }
        var watchers = await LoadWatcherMembershipIdsAsync(companyId, [cardId], cancellationToken);
        return ToResponse(card, now, watchers.GetValueOrDefault(cardId));
    }

    private async Task<AtendimentoCardResponse> InsertCardAsync(
        ActorContext actor,
        CreateAtendimentoCardCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var card = new AtendimentoCard
        {
            Id = Guid.NewGuid(),
            CompanyId = command.CompanyId,
            Title = command.Title.Trim(),
            Description = command.Description.Trim(),
            Column = "novo",
            Priority = command.Priority!,
            Channel = command.Channel!,
            SlaDueAt = command.SlaDueAt,
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
        var actorMembershipId = await ResolveActorMembershipIdAsync(
            actor,
            command.CompanyId,
            cancellationToken
        );
        await AppendCardEventAsync(
            command.CompanyId,
            card.Id,
            actorMembershipId,
            "created",
            new { column = card.Column },
            cancellationToken
        );
        return ToResponse(card, now);
    }

    private async Task<AtendimentoCardResponse?> UpdateAssigneeAsync(
        ActorContext actor,
        Guid cardId,
        AssignAtendimentoCardCommand command,
        DateTimeOffset now,
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
                card.UpdatedAt = now;
                card.RequestId = command.RequestId;
            },
            now, cancellationToken
        );

    private async Task<AtendimentoCardResponse?> MoveCardAsync(
        ActorContext actor,
        Guid cardId,
        MoveAtendimentoCardCommand command,
        DateTimeOffset now,
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
                card.UpdatedAt = now;
                card.RequestId = command.RequestId;
            },
            now, cancellationToken
        );

    private async Task<AtendimentoCardResponse?> CancelCardAsync(
        ActorContext actor,
        Guid cardId,
        CancelAtendimentoCardCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) =>
        await UpdateCardAsync(
            cardId,
            command.CompanyId,
            command.ExpectedVersion,
            card =>
            {
                card.Column = "cancelado";
                card.CancelledAt = now;
                card.CancelledByUserId = actor.UserId;
                card.CancellationReason = command.Reason.Trim();
                card.UpdatedByUserId = actor.UserId;
                card.UpdatedAt = now;
                card.RequestId = command.RequestId;
            },
            now, cancellationToken
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
        DateTimeOffset now,
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

        var watchers = await LoadWatcherMembershipIdsAsync(companyId, [cardId], cancellationToken);
        return ToResponse(card, now, watchers.GetValueOrDefault(cardId));
    }

    private static AtendimentoCardResponse ToResponse(
        AtendimentoCard card,
        DateTimeOffset now,
        IReadOnlyList<Guid>? watcherMembershipIds = null
    ) =>
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
            Priority = card.Priority,
            Channel = card.Channel,
            SlaDueAt = card.SlaDueAt,
            IsOverdue = IsOverdue(card.SlaDueAt, card.Column, now),
            CategoryRefId = card.CategoryRefId,
            ChannelRefId = card.ChannelRefId,
            RequesterPersonRef = card.RequesterPersonRef,
            CreatedAt = card.CreatedAt,
            UpdatedAt = card.UpdatedAt,
            AttachmentFileId = card.AttachmentFileId,
            ParticipantMembershipIds =
            [
                .. card.Participants.OrderBy(
                    participant => participant.MembershipId.ToString("N"),
                    StringComparer.Ordinal
                ).Select(participant => participant.MembershipId),
            ],
            WatcherMembershipIds = watcherMembershipIds,
        };

    /// <summary>Escreve a entrada de timeline; payload vira apenas digest SHA-256.</summary>
    private async Task AppendCardEventAsync(
        Guid companyId,
        Guid cardId,
        Guid? actorMembershipId,
        string actionKind,
        object payload,
        CancellationToken cancellationToken
    )
    {
        var digest = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))))
            .ToLowerInvariant();
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO service_desk.atendimento_card_events
                (id, company_id, card_id, action_kind, actor_membership_id, payload_digest)
            VALUES ({Guid.NewGuid()}, {companyId}, {cardId}, {actionKind}, {actorMembershipId}, {digest})
            """,
            cancellationToken
        );
    }

    private async Task<Guid?> ResolveActorMembershipIdAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    ) =>
        await database
            .Memberships.AsNoTracking()
            .Where(member =>
                member.CompanyId == companyId
                && member.UserId == actor.UserId
                && member.DeletedAt == null
            )
            .Select(member => (Guid?)member.Id)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<Guid?> ResolveMembershipUserIdAsync(
        Guid companyId,
        Guid membershipId,
        CancellationToken cancellationToken
    ) =>
        await database
            .Memberships.AsNoTracking()
            .Where(member => member.CompanyId == companyId && member.Id == membershipId)
            .Select(member => (Guid?)member.UserId)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>Resolve user ids de vários memberships ativos para intents de menção.</summary>
    private async Task<List<Guid>> ResolveMembershipUserIdsAsync(
        Guid companyId,
        IReadOnlyList<Guid> membershipIds,
        CancellationToken cancellationToken
    ) =>
        await database
            .Memberships.AsNoTracking()
            .Where(member =>
                member.CompanyId == companyId
                && member.DeletedAt == null
                && membershipIds.Contains(member.Id)
            )
            .Select(member => member.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

    private Task<bool> IsWatcherPresentAsync(
        Guid companyId,
        Guid cardId,
        Guid membershipId,
        CancellationToken cancellationToken
    ) =>
        database.AtendimentoCardWatchers.AnyAsync(
            watcher =>
                watcher.CompanyId == companyId
                && watcher.CardId == cardId
                && watcher.MembershipId == membershipId,
            cancellationToken
        );

    private async Task<int> CountActiveMembershipsAsync(
        Guid companyId,
        IReadOnlyList<Guid> membershipIds,
        CancellationToken cancellationToken
    ) =>
        await database
            .Memberships.AsNoTracking()
            .CountAsync(
                member =>
                    member.CompanyId == companyId
                    && member.DeletedAt == null
                    && membershipIds.Contains(member.Id),
                cancellationToken
            );

    /// <summary>
    /// Intents saem somente em terminal de sucesso (Success/Created): estados
    /// de rollback (auditoria/autorização/idempotência) nunca notificam.
    /// </summary>
    private async Task FlushOnTerminalSuccessAsync(
        ActorContext actor,
        AtendimentoCardOperationState state,
        List<PendingIntent> pending,
        CancellationToken cancellationToken
    )
    {
        if (state is not (AtendimentoCardOperationState.Success or AtendimentoCardOperationState.Created))
        {
            pending.Clear();
            return;
        }

        await FlushPendingNotificationsAsync(actor, pending, cancellationToken);
    }

    /// <summary>
    /// Envia os intents pendentes APÓS o commit: chave determinística deduplica
    /// retries/replays e falha é best-effort registrada em Warning.
    /// </summary>
    private async Task FlushPendingNotificationsAsync(
        ActorContext actor,
        IReadOnlyList<PendingIntent> pending,
        CancellationToken cancellationToken
    )
    {
        foreach (var intent in pending)
        {
            try
            {
                var result = await notificationIntents.EnqueueAsync(
                    actor,
                    new NotificationIntentCommand(
                        intent.CompanyId,
                        intent.RecipientUserId,
                        EventKey(intent.Kind, intent.CardId),
                        intent.TemplateKey,
                        DeepLinkSelector: "/atendimento"
                    ),
                    cancellationToken
                );
                if (result.State is not NotificationIntentEnqueueState.Enqueued)
                {
                    logger.LogWarning(
                        "Atendimento {Operation} {Module} {Result} {Reason}",
                        Operation,
                        Module,
                        "notification_intent_skipped",
                        $"notification_intent_{result.State.ToString().ToLowerInvariant()}"
                    );
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Atendimento {Operation} {Module} {Result} {Reason}",
                    Operation,
                    Module,
                    "notification_intent_failed",
                    "best_effort_notification"
                );
            }
        }
    }

    /// <summary>
    /// Contrato determinístico com cartão e tipo na frente; o destinatário não
    /// entra na chave porque a unicidade do intent inclui recipient_user_id.
    /// </summary>
    private static string EventKey(string kind, Guid cardId) =>
        $"atm.card.{cardId:N}.{kind}";

    private async Task<AtendimentoLookupWriteResult> AuditLookupDeniedAsync(
        ActorContext actor,
        Guid companyId,
        string auditAction,
        AtendimentoLookupWriteState state,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await SetContextAsync(actor, companyId, cancellationToken);
            await AppendAuditAsync(
                actor,
                companyId,
                auditAction,
                null,
                AuditOutcome.Denied,
                ErrorCode(state),
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            LogInformation("denied", ErrorCode(state));
            return new(state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "lookup_denial_audit_failed");
            return new(AtendimentoLookupWriteState.Unavailable);
        }
    }

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
        && command.Priority is (null or AtendimentoPriority.Critical or AtendimentoPriority.High or AtendimentoPriority.Medium or AtendimentoPriority.Low)
        && command.Channel is (AtendimentoChannel.WhatsApp or AtendimentoChannel.Email or AtendimentoChannel.Web or AtendimentoChannel.Manual)
        && command.AttachmentFileId != Guid.Empty
        && command.ParticipantMembershipIds?.All(id => id != Guid.Empty) is not false;

    private static bool IsValid(AssignAtendimentoCardCommand command) =>
        command.ExpectedVersion > 0 && command.AssigneeMembershipId != Guid.Empty;

    private static bool IsValid(MoveAtendimentoCardCommand command) =>
        command.ExpectedVersion > 0
        && command.Column?.Trim() is "novo" or "em_andamento" or "concluido";

    private static bool IsValid(CancelAtendimentoCardCommand command) =>
        command.ExpectedVersion > 0 && command.Reason?.Trim().Length is > 0 and <= 500;

    /// <summary>
    /// Prioridade e SLA válidos quando presentes; SLA no passado é rejeitado.
    /// Solicitante acima do varchar(200) e menções vazias/sem conta excedida
    /// são invalid_request (400), nunca erro de infraestrutura.
    /// </summary>
    private static bool IsValid(UpdateAtendimentoCardFieldsCommand command)
    {
        if (command.ExpectedVersion <= 0)
        {
            return false;
        }
        if (
            command.Priority is { } priority
            && !ValidPriorities.Contains(priority.Trim())
        )
        {
            return false;
        }
        if (
            command.RequesterPersonRef?.Trim().Length
                is > UpdateAtendimentoCardFieldsCommand.MaxRequesterPersonRefLength
        )
        {
            return false;
        }
        if (command.MentionedMembershipIds is { } mentions)
        {
            if (mentions.Any(id => id == Guid.Empty))
            {
                return false;
            }
            if (mentions.Count > UpdateAtendimentoCardFieldsCommand.MaxMentionedMembershipIds)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsValid(AtendimentoCardWatcherCommand command) =>
        command.ExpectedVersion > 0 && command.MemberMembershipId != Guid.Empty;

    private static CreateAtendimentoCardCommand NormalizeCreateCommand(
        CreateAtendimentoCardCommand command
    ) =>
        command with
        {
            Priority = command.Priority ?? AtendimentoPriority.Medium,
            Channel = command.Channel ?? AtendimentoChannel.Manual,
            SlaDueAt = command.SlaDueAt?.ToUniversalTime(),
            ParticipantMembershipIds =
            [
                .. (command.ParticipantMembershipIds ?? [])
                    .Distinct()
                    .OrderBy(id => id.ToString("N"), StringComparer.Ordinal),
            ],
        };

    private static AtendimentoCardResponse NormalizeResponse(
        AtendimentoCardResponse response,
        DateTimeOffset now
    ) =>
        response with
        {
            Priority = response.Priority ?? AtendimentoPriority.Medium,
            Channel = response.Channel ?? AtendimentoChannel.Manual,
            ParticipantMembershipIds = response.ParticipantMembershipIds ?? [],
            IsOverdue = IsOverdue(response.SlaDueAt, response.Column, now),
        };

    private static bool IsCancelled(AtendimentoCardResponse card) =>
        string.Equals(card.Column, CancelledColumn, StringComparison.Ordinal);

    private static bool IsCancelled(AtendimentoCard card) =>
        string.Equals(card.Column, CancelledColumn, StringComparison.Ordinal);

    private static bool IsOverdue(DateTimeOffset? slaDueAt, string column, DateTimeOffset now) =>
        slaDueAt is not null
        && slaDueAt < now
        && column is not "concluido" and not "cancelado";

    private static string CommandScope(string action, Guid cardId) => $"{action}:{cardId:N}";

    private static string HashCreate(CreateAtendimentoCardCommand command) =>
        command.AttachmentFileId is null
        && command.ParticipantMembershipIds!.Count == 0
        && command.Priority == AtendimentoPriority.Medium
        && command.Channel == AtendimentoChannel.Manual
        && command.SlaDueAt is null
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
            AtendimentoCardOperationState.Invalid => "invalid_request",
            AtendimentoCardOperationState.MembershipRequired => "membership_required",
            AtendimentoCardOperationState.CapabilityDenied => "capability_denied",
            AtendimentoCardOperationState.ResourceNotFound => "resource_not_found",
            AtendimentoCardOperationState.IdempotencyConflict => "idempotency_conflict",
            AtendimentoCardOperationState.IntegrityConflict => "integrity_conflict",
            AtendimentoCardOperationState.TerminalState => "terminal_state",
            _ => "atendimento_unavailable",
        };

    private static string ErrorCode(AtendimentoLookupWriteState state) =>
        state switch
        {
            AtendimentoLookupWriteState.Invalid => "invalid_request",
            AtendimentoLookupWriteState.MembershipRequired => "membership_required",
            AtendimentoLookupWriteState.CapabilityDenied => "capability_denied",
            AtendimentoLookupWriteState.ResourceNotFound => "resource_not_found",
            AtendimentoLookupWriteState.IdempotencyConflict => "idempotency_conflict",
            AtendimentoLookupWriteState.Conflict => "lookup_conflict",
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

    private sealed record WatcherCommandPayload(
        Guid CardId,
        AtendimentoCardWatcherCommand Command,
        bool IsAdd
    );

    private sealed record DeactivateCategoryPayload(Guid CategoryId);

    private sealed record LookupMutation(
        AtendimentoLookupWriteState State,
        AtendimentoCategoryResponse? Category = null
    )
    {
        public static LookupMutation Succeeded(
            AtendimentoCategoryResponse category,
            AtendimentoLookupWriteState state
        ) => new(state, category);

        public static LookupMutation ResourceNotFound() =>
            new(AtendimentoLookupWriteState.ResourceNotFound);

        public static LookupMutation Conflict() => new(AtendimentoLookupWriteState.Conflict);
    }

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

        public static Mutation Invalid() =>
            new(AtendimentoCardOperationState.Invalid);
    }
}
