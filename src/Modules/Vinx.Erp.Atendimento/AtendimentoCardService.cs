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

    private static readonly HashSet<string> ValidPriorities =
        new(StringComparer.Ordinal) { "critica", "alta", "media", "baixa" };

    private static readonly HashSet<string> ValidColumns =
        new(StringComparer.Ordinal) { "novo", "em_andamento", ClosedColumn };

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
        Guid RecipientUserId
    );

    public async Task<AtendimentoCardOperationResult> CreateAsync(
        ActorContext actor,
        CreateAtendimentoCardCommand command,
        CancellationToken cancellationToken
    )
    {
        var pending = new List<PendingIntent>();
        var result = await ExecuteAsync(
            actor,
            command.CompanyId,
            command.RequestId,
            KnownCapabilities.ServiceDeskTicketCreate,
            CreateAction,
            CreateAction,
            IsValid(command),
            Hash(command),
            async () =>
            {
                var card = await InsertCardAsync(actor, command, cancellationToken);
                return Mutation.Succeeded(card, AtendimentoCardOperationState.Created);
            },
            cancellationToken
        );
        await FlushPendingNotificationsAsync(actor, pending, cancellationToken);
        return result;
    }

    public async Task<AtendimentoCardOperationResult> AssignAsync(
        ActorContext actor,
        Guid cardId,
        AssignAtendimentoCardCommand command,
        CancellationToken cancellationToken
    )
    {
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
                    var membershipId = await ResolveActorMembershipIdAsync(
                        actor,
                        command.CompanyId,
                        cancellationToken
                    );
                    await AppendCardEventAsync(
                        command.CompanyId,
                        cardId,
                        membershipId,
                        "assigned",
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
                                recipientUserId.Value
                            )
                        );
                    }
                    return Mutation.Succeeded(updated, AtendimentoCardOperationState.Success);
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

                return await ReadCurrentAfterMutationMissAsync(
                    command.CompanyId,
                    cardId,
                    cancellationToken
                );
            },
            cancellationToken
        );
        if (result.State is not (AtendimentoCardOperationState.Success or AtendimentoCardOperationState.Created))
        {
            pending.Clear();
        }
        await FlushPendingNotificationsAsync(actor, pending, cancellationToken);
        return result;
    }

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

                var updated = await MoveCardAsync(actor, before.Id, command, cancellationToken);
                if (updated is null)
                {
                    return await ReadCurrentAfterMutationMissAsync(
                        command.CompanyId,
                        cardId,
                        cancellationToken
                    );
                }

                var membershipId = await ResolveActorMembershipIdAsync(
                    actor,
                    command.CompanyId,
                    cancellationToken
                );
                await database.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO service_desk.atendimento_card_history
                        (id, company_id, card_id, from_column, to_column, actor_user_id, request_id)
                    VALUES ({Guid.NewGuid()}, {command.CompanyId}, {cardId}, {before.Column}, {command.Column.Trim()}, {actor.UserId}, {command.RequestId})
                    """,
                    cancellationToken
                );
                await AppendCardEventAsync(
                    command.CompanyId,
                    cardId,
                    membershipId,
                    "moved",
                    new { from = before.Column, to = command.Column.Trim() },
                    cancellationToken
                );
                return Mutation.Succeeded(updated, AtendimentoCardOperationState.Success);
            },
            cancellationToken
        );

    public async Task<AtendimentoCardOperationResult> GetQueueAsync(
        ActorContext actor,
        Guid companyId,
        AtendimentoCardQueueFilter? filter,
        CancellationToken cancellationToken
    )
    {
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

            var cards = await QueryCardsAsync(companyId, filter, cancellationToken);
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

    /// <summary>Atualiza os campos de ticket com semântica null = inalterado.</summary>
    public async Task<AtendimentoCardOperationResult> UpdateFieldsAsync(
        ActorContext actor,
        Guid cardId,
        UpdateAtendimentoCardFieldsCommand command,
        CancellationToken cancellationToken
    )
    {
        var pending = new List<PendingIntent>();
        var result = await ExecuteAsync(
            actor,
            command.CompanyId,
            command.RequestId,
            KnownCapabilities.ServiceDeskTicketFields,
            FieldsAction,
            CommandScope(FieldsAction, cardId),
            IsValid(command),
            Hash(new RoutedCommandPayload<UpdateAtendimentoCardFieldsCommand>(cardId, command)),
            () =>
                UpdateTicketingFieldsAsync(actor, cardId, command, pending, cancellationToken),
            cancellationToken
        );
        await FlushPendingNotificationsAsync(actor, pending, cancellationToken);
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
        var card = await database.AtendimentoCards.SingleOrDefaultAsync(
            candidate => candidate.CompanyId == command.CompanyId && candidate.Id == cardId,
            cancellationToken
        );
        if (card is null)
        {
            return Mutation.ResourceNotFound();
        }
        if (card.RowVersion != command.ExpectedVersion)
        {
            return await ReadCurrentAfterMutationMissAsync(
                command.CompanyId,
                cardId,
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
            card.Priority = priority;
            changed[nameof(card.Priority)] = priority;
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
            changed[nameof(card.RequesterPersonRef)] = requester.Trim();
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
                cancellationToken
            );
        }

        var membershipId = await ResolveActorMembershipIdAsync(
            actor,
            command.CompanyId,
            cancellationToken
        );
        await AppendCardEventAsync(
            command.CompanyId,
            cardId,
            membershipId,
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
                    new PendingIntent(command.CompanyId, cardId, MentionedTemplateKey, mentioned)
                );
            }
        }

        return Mutation.Succeeded(
            await FindTrackedCardAsync(command.CompanyId, cardId, cancellationToken),
            AtendimentoCardOperationState.Success
        );
    }

    private async Task<AtendimentoCardResponse> FindTrackedCardAsync(Guid companyId, Guid cardId, CancellationToken cancellationToken)
    {
        var card = await database
            .AtendimentoCards.AsNoTracking()
            .SingleAsync(candidate => candidate.CompanyId == companyId && candidate.Id == cardId, cancellationToken);
        var watchers = await LoadWatcherMembershipIdsAsync(companyId, [cardId], cancellationToken);
        return ToResponse(card, watchers.GetValueOrDefault(cardId));
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
        await FlushPendingNotificationsAsync(actor, pending, cancellationToken);
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
        await FlushPendingNotificationsAsync(actor, pending, cancellationToken);
        return result;
    }

    private Task<AtendimentoCardOperationResult> ExecuteWatcherAsync(
        ActorContext actor,
        Guid cardId,
        AtendimentoCardWatcherCommand command,
        bool isAdd,
        List<PendingIntent> pending,
        CancellationToken cancellationToken
    ) =>
        ExecuteAsync(
            actor,
            command.CompanyId,
            command.RequestId,
            capability: KnownCapabilities.ServiceDeskTicketWatchers,
            auditAction: WatchersAction,
            idempotencyCommandName: CommandScope($"{WatchersAction}.{(isAdd ? "add" : "remove")}", cardId),
            valid: IsValid(command),
            payloadHash: Hash(new WatcherCommandPayload(cardId, command, isAdd)),
            async () =>
            {
                // Repetição idempotente antecipada: sem DML, sem bump de versão.
                var alreadyWatcher = await database.AtendimentoCardWatchers.AnyAsync(
                    watcher =>
                        watcher.CompanyId == command.CompanyId
                        && watcher.CardId == cardId
                        && watcher.MembershipId == command.MemberMembershipId,
                    cancellationToken
                );
                int affected;
                Guid? membershipId;
                if (!alreadyWatcher)
                {
                    var memberActive = await IsActiveMembershipAsync(
                        command.CompanyId,
                        command.MemberMembershipId,
                        cancellationToken
                    );
                    if (!memberActive)
                    {
                        return Mutation.ResourceNotFound();
                    }

                    var unchangedBefore = await FindCardAsync(
                        command.CompanyId,
                        cardId,
                        cancellationToken
                    );
                    if (unchangedBefore is null)
                    {
                        return Mutation.ResourceNotFound();
                    }

                    // Bump otimista ANTES do DML do observador: versão divergente
                    // devolve conflito com estado atual e desfaz o restante.
                    var updatedCard = await UpdateCardAsync(
                        cardId,
                        command.CompanyId,
                        command.ExpectedVersion,
                        card =>
                        {
                            card.RequestId = command.RequestId;
                        },
                        cancellationToken
                    );
                    if (updatedCard is null)
                    {
                        return await ReadCurrentAfterMutationMissAsync(
                            command.CompanyId,
                            cardId,
                            cancellationToken
                        );
                    }

                    membershipId = await ResolveActorMembershipIdAsync(
                        actor,
                        command.CompanyId,
                        cancellationToken
                    );
                    affected = 1;
                    await database.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO service_desk.atendimento_card_watchers
                            (id, company_id, card_id, membership_id, created_by_user_id, request_id)
                        VALUES ({Guid.NewGuid()}, {command.CompanyId}, {cardId}, {command.MemberMembershipId}, {actor.UserId}, {command.RequestId})
                        ON CONFLICT (company_id, card_id, membership_id) DO NOTHING
                        """,
                        cancellationToken
                    );
                    await AppendCardEventAsync(
                        command.CompanyId,
                        cardId,
                        membershipId,
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
                                recipientUserId.Value
                            )
                        );
                    }
                }
                else
                {
                    membershipId = null;
                    affected = await database.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        DELETE FROM service_desk.atendimento_card_watchers
                        WHERE company_id = {command.CompanyId}
                            AND card_id = {cardId}
                            AND membership_id = {command.MemberMembershipId}
                        """,
                        cancellationToken
                    );
                }

                if (isAdd && alreadyWatcher)
                {
                    var currentNoChange = await FindCardAsync(command.CompanyId, cardId, cancellationToken);
                    return currentNoChange is null
                        ? Mutation.ResourceNotFound()
                        : Mutation.Succeeded(currentNoChange, AtendimentoCardOperationState.NoChange);
                }

                if (affected == 0)
                {
                    // Remoção de vínculo inexistente é repetição idempotente.
                    var currentNone = await FindCardAsync(command.CompanyId, cardId, cancellationToken);
                    return currentNone is null
                        ? Mutation.ResourceNotFound()
                        : Mutation.Succeeded(currentNone, AtendimentoCardOperationState.NoChange);
                }

                // Caminho da remoção efetiva precisa do bump otimista e do evento.
                var bumpedForRemove = await UpdateCardAsync(
                    cardId,
                    command.CompanyId,
                    command.ExpectedVersion,
                    card =>
                    {
                        card.RequestId = command.RequestId;
                    },
                    cancellationToken
                );
                if (bumpedForRemove is null)
                {
                    return await ReadCurrentAfterMutationMissAsync(
                        command.CompanyId,
                        cardId,
                        cancellationToken
                    );
                }

                var removalActor = await ResolveActorMembershipIdAsync(
                    actor,
                    command.CompanyId,
                    cancellationToken
                );
                await AppendCardEventAsync(
                    command.CompanyId,
                    cardId,
                    removalActor,
                    "watcher_removed",
                    new { watcherMembershipId = command.MemberMembershipId },
                    cancellationToken
                );

                var current = await FindCardAsync(command.CompanyId, cardId, cancellationToken);
                return current is null
                    ? Mutation.ResourceNotFound()
                    : Mutation.Succeeded(current, AtendimentoCardOperationState.Success);
            },
            cancellationToken
        );

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
                SELECT entry.id AS "Id",
                       entry.event_seq AS "EventSeq",
                       entry.action_kind AS "ActionKind",
                       entry.actor_membership_id AS "ActorMembershipId",
                       COALESCE(actor.display_name, '') AS "ActorDisplayName",
                       entry.occurred_at AS "OccurredAt"
                FROM (
                    SELECT *
                    FROM service_desk.atendimento_card_events
                    WHERE company_id = {companyId} AND card_id = {cardId}
                    ORDER BY event_seq DESC
                    LIMIT 100
                ) AS entry
                LEFT JOIN foundation.memberships AS membership ON membership.id = entry.actor_membership_id
                LEFT JOIN foundation.users AS actor ON actor.id = membership.user_id
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

    /// <summary>Cria categoria por empresa; nome é aparado e duplicata é conflito estável.</summary>
    public async Task<AtendimentoLookupWriteResult> CreateCategoryAsync(
        ActorContext actor,
        CreateAtendimentoCategoryCommand command,
        CancellationToken cancellationToken
    )
    {
        var name = command.Name?.Trim();
        if (
            command.CompanyId == Guid.Empty
            || command.RequestId == Guid.Empty
            || string.IsNullOrWhiteSpace(name)
            || name.Length > 80
        )
        {
            return new(AtendimentoLookupWriteState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await SetContextAsync(actor, command.CompanyId, cancellationToken);
            var denied = await AuthorizeAsync(
                actor,
                command.CompanyId,
                KnownCapabilities.ServiceDeskLookupAdmin,
                cancellationToken
            );
            if (denied is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await AuditLookupDeniedAsync(
                    actor,
                    command.CompanyId,
                    CategoryCreateAction,
                    denied.Value,
                    cancellationToken
                );
            }

            var existing = await FindIdempotencyRecordAsync(
                command.CompanyId,
                actor.UserId,
                CategoryCreateAction,
                command.RequestId,
                cancellationToken
            );
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
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

            var duplicate = await database.AtendimentoCardCategories.AnyAsync(
                category =>
                    category.CompanyId == command.CompanyId && category.Name == name,
                cancellationToken
            );
            if (duplicate)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(AtendimentoLookupWriteState.Conflict);
            }

            var now = DateTimeOffset.UtcNow;
            var category = new AtendimentoCardCategory
            {
                Id = Guid.NewGuid(),
                CompanyId = command.CompanyId,
                Name = name,
                IsActive = true,
                CreatedByUserId = actor.UserId,
                UpdatedByUserId = actor.UserId,
                RequestId = command.RequestId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            database.AtendimentoCardCategories.Add(category);
            await AppendAuditAsync(
                actor,
                command.CompanyId,
                CategoryCreateAction,
                category.Id,
                AuditOutcome.Success,
                null,
                cancellationToken
            );
            database.IdempotencyRecords.Add(
                new IdempotencyRecord(
                    Guid.NewGuid(),
                    command.CompanyId,
                    actor.UserId,
                    CategoryCreateAction,
                    command.RequestId,
                    Hash(command.Name),
                    JsonSerializer.Serialize(new AtendimentoCategoryResponse(category.Id, category.CompanyId, name))
                )
            );
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogInformation("succeeded", "category_created");
            return new(
                AtendimentoLookupWriteState.Created,
                new AtendimentoCategoryResponse(category.Id, category.CompanyId, name)
            );
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres
            && postgres.ConstraintName == "uq_atendimento_categories_company_id_name")
        {
            database.ChangeTracker.Clear();
            return new(AtendimentoLookupWriteState.Conflict);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "category_create_failed");
            return new(AtendimentoLookupWriteState.Unavailable);
        }
    }

    public async Task<AtendimentoLookupWriteResult> DeactivateCategoryAsync(
        ActorContext actor,
        DeactivateAtendimentoCategoryCommand command,
        CancellationToken cancellationToken
    )
    {
        if (command.CompanyId == Guid.Empty || command.CategoryId == Guid.Empty)
        {
            return new(AtendimentoLookupWriteState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await SetContextAsync(actor, command.CompanyId, cancellationToken);
            var denied = await AuthorizeAsync(
                actor,
                command.CompanyId,
                KnownCapabilities.ServiceDeskLookupAdmin,
                cancellationToken
            );
            if (denied is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await AuditLookupDeniedAsync(
                    actor,
                    command.CompanyId,
                    CategoryDeactivateAction,
                    denied.Value,
                    cancellationToken
                );
            }

            var category = await database.AtendimentoCardCategories.SingleOrDefaultAsync(
                candidate =>
                    candidate.CompanyId == command.CompanyId
                    && candidate.Id == command.CategoryId,
                cancellationToken
            );
            if (category is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(AtendimentoLookupWriteState.ResourceNotFound);
            }

            if (category.IsActive)
            {
                category.IsActive = false;
                category.UpdatedByUserId = actor.UserId;
                category.UpdatedAt = DateTimeOffset.UtcNow;
                category.RequestId = command.RequestId;
                await AppendAuditAsync(
                    actor,
                    command.CompanyId,
                    CategoryDeactivateAction,
                    category.Id,
                    AuditOutcome.Success,
                    null,
                    cancellationToken
                );
                await database.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new(
                AtendimentoLookupWriteState.Created,
                new AtendimentoCategoryResponse(category.Id, category.CompanyId, category.Name)
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnavailable(exception, "category_deactivate_failed");
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
                return new(AtendimentoCardOperationState.Replay, replay);
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

            if (result.State is AtendimentoCardOperationState.IntegrityConflict)
            {
                await transaction.RollbackAsync(cancellationToken);
                var audited = await AuditDeniedResultAsync(
                    actor,
                    companyId,
                    auditAction,
                    result.State,
                    cancellationToken
                );
                return audited.State == AtendimentoCardOperationState.IntegrityConflict
                    ? new(AtendimentoCardOperationState.IntegrityConflict, Current: result.Current)
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
            return new(AtendimentoCardOperationState.Replay, replay);
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
        AtendimentoCardQueueFilter? filter,
        CancellationToken cancellationToken
    )
    {
        var query = database
            .AtendimentoCards.AsNoTracking()
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
                query = query.Where(
                    card => card.AssigneeMembershipId == assignee
                );
            }
            if (filter.Overdue)
            {
                var now = DateTimeOffset.UtcNow;
                query = query.Where(
                    card =>
                        card.SlaDueAt != null
                        && card.SlaDueAt < now
                        && card.Column != ClosedColumn
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

        var cards = await query.OrderBy(card => card.CreatedAt).ToListAsync(cancellationToken);
        var watchers = await LoadWatcherMembershipIdsAsync(
            companyId,
            [.. cards.Select(card => card.Id)],
            cancellationToken
        );
        return [.. cards.Select(card => ToResponse(card, watchers.GetValueOrDefault(card.Id)))];
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
        CancellationToken cancellationToken
    )
    {
        var card = await database
            .AtendimentoCards.AsNoTracking()
            .SingleOrDefaultAsync(
                card => card.CompanyId == companyId && card.Id == cardId,
                cancellationToken
            );
        if (card is null)
        {
            return null;
        }
        var watchers = await LoadWatcherMembershipIdsAsync(
            companyId,
            [cardId],
            cancellationToken
        );
        return ToResponse(card, watchers.GetValueOrDefault(cardId));
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
            CreatedByUserId = actor.UserId,
            UpdatedByUserId = actor.UserId,
            RequestId = command.RequestId,
            CreatedAt = now,
            UpdatedAt = now,
            RowVersion = 1,
        };
        database.AtendimentoCards.Add(card);
        await database.SaveChangesAsync(cancellationToken);
        var membershipId = await ResolveActorMembershipIdAsync(
            actor,
            command.CompanyId,
            cancellationToken
        );
        await AppendCardEventAsync(
            command.CompanyId,
            card.Id,
            membershipId,
            "created",
            new { column = card.Column },
            cancellationToken
        );
        return ToResponse(card, []);
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
        var card = await database.AtendimentoCards.SingleOrDefaultAsync(
            candidate => candidate.CompanyId == companyId && candidate.Id == cardId,
            cancellationToken
        );
        if (card is null || card.RowVersion != expectedVersion)
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

        var watchers = await LoadWatcherMembershipIdsAsync(
            companyId,
            [cardId],
            cancellationToken
        );
        return ToResponse(card, watchers.GetValueOrDefault(cardId));
    }

    private static AtendimentoCardResponse ToResponse(
        AtendimentoCard card,
        IReadOnlyList<Guid>? watcherMembershipIds
    )
    {
        var now = DateTimeOffset.UtcNow;
        var overdue =
            card.SlaDueAt is { } slaDueAt
            && slaDueAt < now
            && !string.Equals(card.Column, ClosedColumn, StringComparison.Ordinal);
        return new(
            card.Id,
            card.CompanyId,
            card.Title,
            card.Description,
            card.Column,
            card.RowVersion,
            card.AssigneeMembershipId,
            card.Priority,
            card.CategoryRefId,
            card.ChannelRefId,
            card.SlaDueAt,
            overdue,
            card.RequesterPersonRef,
            watcherMembershipIds
        );
    }

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
                        EventKey(intent.TemplateKey, intent.CardId, intent.RecipientUserId),
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

    private static string EventKey(string templateKey, Guid cardId, Guid recipientUserId) =>
        $"{templateKey}:{cardId:N}:{recipientUserId:N}";

    private async Task<AtendimentoLookupWriteResult> AuditLookupDeniedAsync(
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
            return state is AtendimentoCardOperationState.MembershipRequired
                ? new(AtendimentoLookupWriteState.MembershipRequired)
                : new(AtendimentoLookupWriteState.CapabilityDenied);
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
        && command.Description?.Trim().Length is > 0 and <= 4000;

    private static bool IsValid(AssignAtendimentoCardCommand command) =>
        command.ExpectedVersion > 0 && command.AssigneeMembershipId != Guid.Empty;

    private static bool IsValid(MoveAtendimentoCardCommand command) =>
        command.ExpectedVersion > 0
        && command.Column?.Trim() is "novo" or "em_andamento" or "concluido";

    /// <summary>Prioridade e SLA válidos quando presentes; SLA no passado é rejeitado.</summary>
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
        if (command.SlaDueAt is { } slaDueAt && slaDueAt <= DateTimeOffset.UtcNow)
        {
            return false;
        }
        return true;
    }

    private static bool IsValid(AtendimentoCardWatcherCommand command) =>
        command.ExpectedVersion > 0 && command.MemberMembershipId != Guid.Empty;

    private static string CommandScope(string action, Guid cardId) => $"{action}:{cardId:N}";

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

    private sealed record WatcherCommandPayload(
        Guid CardId,
        AtendimentoCardWatcherCommand Command,
        bool IsAdd
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
    }
}
