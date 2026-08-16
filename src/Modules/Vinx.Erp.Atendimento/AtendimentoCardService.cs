using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;

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
    private const string ResourceType = "atendimento_card";

    public Task<AtendimentoCardOperationResult> CreateAsync(
        ActorContext actor,
        CreateAtendimentoCardCommand command,
        CancellationToken cancellationToken
    ) =>
        ExecuteAsync(
            actor,
            command.CompanyId,
            command.RequestId,
            KnownCapabilities.ServiceDeskTicketCreate,
            "service_desk.ticket.create",
            IsValid(command),
            Hash(command),
            async () =>
            {
                var card = await InsertCardAsync(actor, command, cancellationToken);
                return new Mutation(card, AtendimentoCardOperationState.Created);
            },
            cancellationToken
        );

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
            "service_desk.ticket.assign",
            IsValid(command),
            Hash(command),
            async () =>
            {
                var card = await FindCardAsync(command.CompanyId, cardId, cancellationToken);
                if (card is null)
                    return new Mutation(null, AtendimentoCardOperationState.ResourceNotFound);
                if (card.Version != command.ExpectedVersion)
                    return new Mutation(card, AtendimentoCardOperationState.IntegrityConflict);
                var member = await database.Memberships.SingleOrDefaultAsync(
                    candidate =>
                        candidate.CompanyId == command.CompanyId
                        && candidate.Id == command.AssigneeMembershipId,
                    cancellationToken
                );
                if (member is null)
                    return new Mutation(null, AtendimentoCardOperationState.ResourceNotFound);
                var updated = await UpdateAssigneeAsync(actor, cardId, command, cancellationToken);
                return new Mutation(
                    updated ?? card,
                    updated is null
                        ? AtendimentoCardOperationState.IntegrityConflict
                        : AtendimentoCardOperationState.Success
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
            "service_desk.ticket.move",
            IsValid(command),
            Hash(command),
            async () =>
            {
                var card = await FindCardAsync(command.CompanyId, cardId, cancellationToken);
                if (card is null)
                    return new Mutation(null, AtendimentoCardOperationState.ResourceNotFound);
                if (card.Version != command.ExpectedVersion)
                    return new Mutation(card, AtendimentoCardOperationState.IntegrityConflict);
                var updated = await MoveCardAsync(actor, card, command, cancellationToken);
                if (updated is null)
                    return new Mutation(card, AtendimentoCardOperationState.IntegrityConflict);
                await database.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO service_desk.atendimento_card_history
                        (id, company_id, card_id, from_column, to_column, actor_user_id, request_id)
                    VALUES ({Guid.NewGuid()}, {command.CompanyId}, {cardId}, {card.Column}, {command.Column.Trim()}, {actor.UserId}, {command.RequestId})
                    """,
                    cancellationToken
                );
                return new Mutation(updated, AtendimentoCardOperationState.Success);
            },
            cancellationToken
        );

    public async Task<AtendimentoCardOperationResult> GetQueueAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    )
    {
        if (companyId == Guid.Empty)
            return new(AtendimentoCardOperationState.Invalid);
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
                return await AuditDenialAsync(actor, companyId, denied.Value, cancellationToken);
            }
            var cards = await QueryCardsAsync(companyId, cancellationToken);
            await AppendAuditAsync(
                actor,
                companyId,
                "service_desk.ticket.read_queue",
                null,
                AuditOutcome.Success,
                null,
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            return new(AtendimentoCardOperationState.Success, Cards: cards);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            logger.LogError(exception, "Atendimento queue operation failed safely.");
            return new(AtendimentoCardOperationState.Unavailable);
        }
    }

    private async Task<AtendimentoCardOperationResult> ExecuteAsync(
        ActorContext actor,
        Guid companyId,
        Guid requestId,
        string capability,
        string commandName,
        bool valid,
        string payloadHash,
        Func<Task<Mutation>> mutation,
        CancellationToken cancellationToken
    )
    {
        if (companyId == Guid.Empty || requestId == Guid.Empty || !valid)
            return new(AtendimentoCardOperationState.Invalid);
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
                return await AuditDenialAsync(actor, companyId, denied.Value, cancellationToken);
            }
            var existing = await database.IdempotencyRecords.SingleOrDefaultAsync(
                record =>
                    record.CompanyId == companyId
                    && record.ActorUserId == actor.UserId
                    && record.CommandName == commandName
                    && record.RequestId == requestId,
                cancellationToken
            );
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                    return await AuditDenialAsync(
                        actor,
                        companyId,
                        AtendimentoCardOperationState.IdempotencyConflict,
                        cancellationToken
                    );
                var replay = JsonSerializer.Deserialize<AtendimentoCardResponse>(
                    existing.ResultJson
                );
                return replay is null
                    ? new(AtendimentoCardOperationState.Unavailable)
                    : new(AtendimentoCardOperationState.Replay, replay);
            }
            var result = await mutation();
            if (
                result.State
                is AtendimentoCardOperationState.ResourceNotFound
                    or AtendimentoCardOperationState.IntegrityConflict
            )
            {
                await transaction.RollbackAsync(cancellationToken);
                return result.State == AtendimentoCardOperationState.IntegrityConflict
                    ? new(result.State, Current: result.Card)
                    : await AuditDenialAsync(actor, companyId, result.State, cancellationToken);
            }
            database.IdempotencyRecords.Add(
                new IdempotencyRecord(
                    Guid.NewGuid(),
                    companyId,
                    actor.UserId,
                    commandName,
                    requestId,
                    payloadHash,
                    JsonSerializer.Serialize(result.Card)
                )
            );
            await AppendAuditAsync(
                actor,
                companyId,
                commandName,
                result.Card!.Id,
                AuditOutcome.Success,
                null,
                cancellationToken
            );
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(result.State, result.Card);
        }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();
            return new(AtendimentoCardOperationState.IntegrityConflict);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            return await ResolveReplayAsync(
                actor,
                companyId,
                requestId,
                commandName,
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
            logger.LogError(exception, "Atendimento command failed safely.");
            return new(AtendimentoCardOperationState.Unavailable);
        }
    }

    private async Task<AtendimentoCardOperationResult> ResolveReplayAsync(
        ActorContext actor,
        Guid companyId,
        Guid requestId,
        string commandName,
        string payloadHash,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await SetContextAsync(actor, companyId, cancellationToken);
            var record = await database
                .IdempotencyRecords.AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.CompanyId == companyId
                        && candidate.ActorUserId == actor.UserId
                        && candidate.CommandName == commandName
                        && candidate.RequestId == requestId,
                    cancellationToken
                );
            await transaction.RollbackAsync(cancellationToken);
            if (record is null)
                return new(AtendimentoCardOperationState.IntegrityConflict);
            if (!string.Equals(record.PayloadHash, payloadHash, StringComparison.Ordinal))
                return await AuditDenialAsync(
                    actor,
                    companyId,
                    AtendimentoCardOperationState.IdempotencyConflict,
                    cancellationToken
                );
            var replay = JsonSerializer.Deserialize<AtendimentoCardResponse>(record.ResultJson);
            return replay is null
                ? new(AtendimentoCardOperationState.Unavailable)
                : new(AtendimentoCardOperationState.Replay, replay);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
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
            return AtendimentoCardOperationState.MembershipRequired;
        return await capabilityEvaluator.HasGrantAsync(
            companyId,
            membership.Id,
            new CapabilityRequirement(capability, CapabilityScope.Company),
            cancellationToken
        )
            ? null
            : AtendimentoCardOperationState.CapabilityDenied;
    }

    private async Task<AtendimentoCardOperationResult> AuditDenialAsync(
        ActorContext actor,
        Guid companyId,
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
            var code = ErrorCode(state);
            await AppendAuditAsync(
                actor,
                companyId,
                code,
                null,
                AuditOutcome.Denied,
                code,
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            return new(state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(AtendimentoCardOperationState.Unavailable);
        }
    }

    private async Task SetContextAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    )
    {
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {actor.UserId.ToString()}, true)",
            cancellationToken
        );
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {companyId.ToString()}, true)",
            cancellationToken
        );
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.correlation_id', {new CorrelationContext(actor.CorrelationId).CorrelationId.ToString()}, true)",
            cancellationToken
        );
    }

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

    private Task<List<AtendimentoCardResponse>> QueryCardsAsync(
        Guid companyId,
        CancellationToken cancellationToken
    ) =>
        database
            .Database.SqlQuery<AtendimentoCardResponse>(
                $"""
                SELECT id, company_id, title, description, column_name AS column, row_version AS version, assignee_membership_id
                FROM service_desk.atendimento_cards WHERE company_id = {companyId} AND deleted_at IS NULL ORDER BY created_at
                """
            )
            .ToListAsync(cancellationToken);

    private Task<AtendimentoCardResponse?> FindCardAsync(
        Guid companyId,
        Guid cardId,
        CancellationToken cancellationToken
    ) =>
        database
            .Database.SqlQuery<AtendimentoCardResponse>(
                $"""
                SELECT id, company_id, title, description, column_name AS column, row_version AS version, assignee_membership_id
                FROM service_desk.atendimento_cards WHERE company_id = {companyId} AND id = {cardId} AND deleted_at IS NULL
                """
            )
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<AtendimentoCardResponse> InsertCardAsync(
        ActorContext actor,
        CreateAtendimentoCardCommand command,
        CancellationToken cancellationToken
    )
    {
        var cardId = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO service_desk.atendimento_cards
                (id, company_id, title, description, column_name, created_by_user_id, updated_by_user_id, request_id)
            VALUES ({cardId}, {command.CompanyId}, {command.Title.Trim()}, {command.Description.Trim()}, 'novo', {actor.UserId}, {actor.UserId}, {command.RequestId})
            """,
            cancellationToken
        );
        return (await FindCardAsync(command.CompanyId, cardId, cancellationToken))!;
    }

    private async Task<AtendimentoCardResponse?> UpdateAssigneeAsync(
        ActorContext actor,
        Guid cardId,
        AssignAtendimentoCardCommand command,
        CancellationToken cancellationToken
    )
    {
        var affected = await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE service_desk.atendimento_cards
            SET assignee_membership_id = {command.AssigneeMembershipId}, updated_by_user_id = {actor.UserId}, updated_at = clock_timestamp(), request_id = {command.RequestId}, row_version = row_version + 1
            WHERE company_id = {command.CompanyId} AND id = {cardId} AND row_version = {command.ExpectedVersion} AND deleted_at IS NULL
            """,
            cancellationToken
        );
        return affected == 1
            ? await FindCardAsync(command.CompanyId, cardId, cancellationToken)
            : null;
    }

    private async Task<AtendimentoCardResponse?> MoveCardAsync(
        ActorContext actor,
        AtendimentoCardResponse card,
        MoveAtendimentoCardCommand command,
        CancellationToken cancellationToken
    )
    {
        var affected = await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE service_desk.atendimento_cards
            SET column_name = {command.Column.Trim()}, updated_by_user_id = {actor.UserId}, updated_at = clock_timestamp(), request_id = {command.RequestId}, row_version = row_version + 1
            WHERE company_id = {command.CompanyId} AND id = {card.Id} AND row_version = {command.ExpectedVersion} AND deleted_at IS NULL
            """,
            cancellationToken
        );
        return affected == 1
            ? await FindCardAsync(command.CompanyId, card.Id, cancellationToken)
            : null;
    }

    private static bool IsValid(CreateAtendimentoCardCommand command) =>
        command.Title?.Trim().Length is > 0 and <= 200
        && command.Description?.Trim().Length is > 0 and <= 4000;

    private static bool IsValid(AssignAtendimentoCardCommand command) =>
        command.ExpectedVersion > 0 && command.AssigneeMembershipId != Guid.Empty;

    private static bool IsValid(MoveAtendimentoCardCommand command) =>
        command.ExpectedVersion > 0
        && command.Column?.Trim() is "novo" or "em_andamento" or "concluido";

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

    private sealed record Mutation(
        AtendimentoCardResponse? Card,
        AtendimentoCardOperationState State
    );
}
