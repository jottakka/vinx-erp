using System.Net.Mail;
using System.Security.Cryptography;
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
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vinx.Erp.Foundation.Permissions;

public sealed record InviteMemberCommand(
    Guid RequestId,
    Guid CompanyId,
    string DisplayName,
    string Email
);

public enum MemberInvitationWriteState
{
    Created,
    Replay,
    IdempotencyConflict,
    Conflict,
    Forbidden,
    Invalid,
    Unavailable,
}

public sealed record MemberInvitationWriteResult(
    MemberInvitationWriteState State,
    Guid? MembershipId = null,
    Guid? UserId = null,
    Guid? EmployeeId = null,
    Guid? InvitationId = null
);

/// <summary>
/// Reserva o gráfico local de membro (usuário, membresia, funcionário) e o convite
/// pendente em uma única função transacional, publicando o envelope opaco de
/// entrega no outbox durável dentro da mesma transação.
/// </summary>
public sealed class MemberInvitationService(
    FoundationDbContext database,
    IDbContextOutbox outbox,
    AuditAppender auditAppender,
    ILogger<MemberInvitationService> logger
)
{
    private const string Operation = "member_invitation_invite";
    private const string Module = "foundation";
    private const string AuditAction = "foundation.member.invite";
    private const string DenialReason = "capability_denied";

    public async Task<MemberInvitationWriteResult> InviteAsync(
        ActorContext actor,
        InviteMemberCommand command,
        CancellationToken cancellationToken
    )
    {
        if (!TryNormalize(command, out var normalized))
        {
            return new(MemberInvitationWriteState.Invalid);
        }

        var commitAttempted = false;
        MemberInvitationWriteResult? result = null;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
            var created = await database.CreatePendingMemberInvitationAsync(
                normalized.RequestId,
                normalized.CompanyId,
                normalized.DisplayName,
                normalized.Email,
                normalized.PayloadHash,
                cancellationToken
            );
            result = new MemberInvitationWriteResult(
                created.State,
                created.MembershipId,
                created.UserId,
                created.EmployeeId,
                created.InvitationId
            );
            if (result.State is MemberInvitationWriteState.Created)
            {
                outbox.Enroll(database);
                await outbox.PublishAsync(
                    new MemberInvitationDeliveryRequested(result.InvitationId!.Value),
                    new DeliveryOptions { CorrelationId = actor.CorrelationId.ToString("D") }
                );
            }

            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
            when (
                !commitAttempted
                && exception.SqlState == PermissionsSqlStates.CapabilityDenied
            )
        {
            return await AuditDeniedAsync(actor, normalized.CompanyId, cancellationToken)
                ? new(MemberInvitationWriteState.Forbidden)
                : new(MemberInvitationWriteState.Unavailable);
        }
        catch (PostgresException exception)
            when (
                !commitAttempted
                && exception.SqlState == PostgresErrorCodes.UniqueViolation
                && exception.ConstraintName
                    is "ix_users_email"
                        or "uq_pending_member_invitations_membership_id"
            )
        {
            return new(MemberInvitationWriteState.Conflict);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            if (commitAttempted)
            {
                logger.LogError(
                    exception,
                    "Member invitation {Operation} {Module} {Result} {Reason}",
                    Operation,
                    Module,
                    "unavailable",
                    "outbox_commit_outcome_unknown"
                );
            }

            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Member invitation {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "unavailable",
                commitAttempted ? "outbox_commit_outcome_unknown" : "database_or_audit_unavailable"
            );
            return new(MemberInvitationWriteState.Unavailable);
        }

        // `result` nunca é null aqui: todo catch acima retorna ou relança
        // antes deste ponto. Replay não republica o envelope de entrega.
        if (result.State is MemberInvitationWriteState.Created)
        {
            try
            {
                await outbox.FlushOutgoingMessagesAsync();
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Member invitation {Operation} {Module} {Result} {Reason}",
                    Operation,
                    Module,
                    "created",
                    "outbox_flush_deferred"
                );
            }
        }

        return result;
    }

    private async Task<bool> AuditDeniedAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await FoundationRlsContext.ApplyCommandAsync(
                database.Database,
                actor,
                companyId,
                cancellationToken
            );
            await auditAppender.AppendAsync(
                new AuditEventData(
                    actor.UserId,
                    AuditScope.Company,
                    companyId,
                    AuditSource.Web,
                    AuditAction,
                    "member_invitation",
                    null,
                    AuditOutcome.Denied,
                    new CorrelationContext(actor.CorrelationId).CorrelationId,
                    DenialReason,
                    "{}"
                ),
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Member invitation {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "unavailable",
                "denial_audit_unavailable"
            );
            return false;
        }
    }

    private static bool TryNormalize(InviteMemberCommand command, out NormalizedCommand normalized)
    {
        normalized = default!;
        var displayName = command.DisplayName?.Trim();
        var email = command.Email?.Trim().ToLowerInvariant();
        if (
            command.RequestId == Guid.Empty
            || command.CompanyId == Guid.Empty
            || string.IsNullOrWhiteSpace(displayName)
            || displayName.Length > 200
            || string.IsNullOrWhiteSpace(email)
            || email.Length > 320
        )
        {
            return false;
        }

        try
        {
            if (!string.Equals(new MailAddress(email).Address, email, StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        var canonicalPayload = JsonSerializer.SerializeToUtf8Bytes(
            new[]
            {
                command.RequestId.ToString("D"),
                command.CompanyId.ToString("D"),
                displayName,
                email,
            }
        );
        var payloadHash = Convert.ToHexString(SHA256.HashData(canonicalPayload)).ToLowerInvariant();
        normalized = new NormalizedCommand(
            command.RequestId,
            command.CompanyId,
            displayName,
            email,
            payloadHash
        );
        return true;
    }

    private sealed record NormalizedCommand(
        Guid RequestId,
        Guid CompanyId,
        string DisplayName,
        string Email,
        string PayloadHash
    );
}
