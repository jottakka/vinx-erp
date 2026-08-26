using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Employees;
using Vinx.Erp.Foundation.Platform;

namespace Vinx.Erp.Foundation.Persistence;

/// <summary>
/// Único ponto de acesso tipado às funções Postgres do schema <c>foundation</c>
/// (lock <c>FOR UPDATE</c>, single-flight e máquinas de estado atômicas — ver a
/// documentação de arquitetura sobre não esconder o PostgreSQL atrás de uma
/// abstração genérica de persistência). Cada método envolve a chamada em
/// <c>Database.SqlQuery&lt;T&gt;</c> e preserva a semântica do retorno da função:
/// ausência de linha, status mapeados e mensagens de exceção.
/// </summary>
internal static class FoundationDbFunctions
{
    /// <summary>
    /// Adquire o lock <c>FOR UPDATE</c> do usuário e lê o corte
    /// <c>sessions_valid_after</c> (linha ausente quando o usuário não existe).
    /// </summary>
    internal static async Task<LockedUser> LockUserForSessionAdmissionAsync(
        this FoundationDbContext database,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var row = await UnwrapProviderExceptionAsync(
            database.Database
                .SqlQuery<SessionLockRow>(
                    $"SELECT sessions_valid_after FROM foundation.lock_user_for_session_admission({userId})")
                .SingleOrDefaultAsync(cancellationToken)
        );
        return row is null
            ? new LockedUser(false, null)
            : new LockedUser(true, row.SessionsValidAfter);
    }

    /// <summary>Revoga todas as sessões ativas do usuário e retorna a quantidade revogada.</summary>
    internal static async Task<int> RevokeAllUserSessionsAsync(
        this FoundationDbContext database,
        Guid userId,
        CancellationToken cancellationToken) =>
        await UnwrapProviderExceptionAsync(
            database.Database
                .SqlQuery<int>(
                    $"SELECT foundation.revoke_all_user_sessions({userId}) AS \"Value\"")
                .SingleAsync(cancellationToken)
        );

    /// <summary>Registra o aceite do convite de owner e a transição para entrega via provedor.</summary>
    internal static async Task<OwnerInvitationDeliveryIngressResult> AcceptOwnerInvitationDeliveryAsync(
        this FoundationDbContext database,
        Guid invitationId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var row = await UnwrapProviderExceptionAsync(
            database.Database
                .SqlQuery<OwnerInvitationDeliveryIngressRow>(
                    $"""
                    SELECT status, provider_pending_at, transitioned
                    FROM foundation.accept_owner_invitation_delivery({invitationId}, {correlationId})
                    """)
                .SingleOrDefaultAsync(cancellationToken)
        );
        return ToIngressResult(row);
    }

    /// <summary>Reivindicação single-flight da entrega do convite pelo provedor externo.</summary>
    internal static async Task<OwnerInvitationProviderClaim> ClaimOwnerInvitationProviderDeliveryAsync(
        this FoundationDbContext database,
        Guid invitationId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var row = await UnwrapProviderExceptionAsync(
            database.Database
                .SqlQuery<OwnerInvitationProviderClaimRow>(
                    $"""
                    SELECT status, attempt_id, owner_email, claimed
                    FROM foundation.claim_owner_invitation_provider_delivery({invitationId}, {correlationId})
                    """)
                .SingleOrDefaultAsync(cancellationToken)
        );
        return ToProviderClaim(row);
    }

    /// <summary>Registra o desfecho da tentativa de entrega pelo provedor.</summary>
    internal static async Task<OwnerInvitationProviderFinalization> FinalizeOwnerInvitationProviderDeliveryAsync(
        this FoundationDbContext database,
        Guid invitationId,
        Guid attemptId,
        Guid correlationId,
        string outcome,
        string? issuer,
        Guid? subject,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        var row = await UnwrapProviderExceptionAsync(
            database.Database
                .SqlQuery<OwnerInvitationProviderFinalizationRow>(
                    $"""
                    SELECT status, finalized
                    FROM foundation.finalize_owner_invitation_provider_delivery({invitationId}, {attemptId}, {correlationId}, {outcome}, {issuer}, {subject}, {failureReason})
                    """)
                .SingleOrDefaultAsync(cancellationToken)
        );
        return ToProviderFinalization(row);
    }

    /// <summary>
    /// Provisiona grupo, empresa e owner do zero ou repete o provisionamento
    /// anterior (created / replay / idempotency_conflict).
    /// </summary>
    internal static async Task<ProvisionedOrganization> ProvisionPlatformOrganizationAsync(
        this FoundationDbContext database,
        Guid requestId,
        string groupName,
        string companyName,
        string ownerDisplayName,
        string ownerEmail,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        var row = await UnwrapProviderExceptionAsync(
            database.Database
                .SqlQuery<ProvisionedOrganizationRow>(
                    $"""
                    SELECT status, group_id, company_id, owner_user_id, owner_membership_id, owner_invitation_id
                    FROM foundation.provision_platform_organization({requestId}, {groupName}, {companyName}, {ownerDisplayName}, {ownerEmail}, {payloadHash})
                    """)
                .SingleOrDefaultAsync(cancellationToken)
        ) ?? throw new InvalidOperationException("Organization provisioning did not return a result.");
        return new ProvisionedOrganization(
            MapProvisioningState(row.Status),
            row.GroupId,
            row.CompanyId,
            row.OwnerUserId,
            row.OwnerMembershipId,
            row.OwnerInvitationId);
    }

    /// <summary>
    /// Mantém a reserva local de funcionário disponível para o dono inicial
    /// sem conceder qualquer acesso de tenant ao operador global.
    /// </summary>
    internal static async Task EnsurePlatformOwnerEmployeeCreateAsync(
        this FoundationDbContext database,
        Guid requestId,
        Guid companyId,
        Guid ownerMembershipId,
        CancellationToken cancellationToken) =>
        await UnwrapProviderExceptionAsync(
            database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT foundation.ensure_platform_owner_employee_create({requestId}, {companyId}, {ownerMembershipId})",
                cancellationToken
            )
        );

    /// <summary>
    /// Cria de forma idempotente e aditiva os papéis padrão (admin/member) da
    /// empresa com as capabilities fixas do catálogo; papéis reservados
    /// pré-existentes nunca são ampliados.
    /// </summary>
    internal static async Task<int> EnsureCompanyDefaultRolesAsync(
        this FoundationDbContext database,
        Guid companyId,
        CancellationToken cancellationToken) =>
        await UnwrapProviderExceptionAsync(
            database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT foundation.ensure_company_default_roles({companyId})",
                cancellationToken
            )
        );

    /// <summary>
    /// Cria a entrada idempotente de funcionário no diretório da empresa
    /// (created / replay / idempotency_conflict).
    /// </summary>
    internal static async Task<EmployeeDirectoryEntry> CreateEmployeeDirectoryEntryAsync(
        this FoundationDbContext database,
        Guid requestId,
        Guid companyId,
        string displayName,
        string email,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        var row = await UnwrapProviderExceptionAsync(
            database.Database
                .SqlQuery<EmployeeDirectoryEntryRow>(
                    $"""
                    SELECT status, employee_id, membership_id, user_id
                    FROM foundation.create_employee_directory_entry({requestId}, {companyId}, {displayName}, {email}, {payloadHash})
                    """)
                .SingleOrDefaultAsync(cancellationToken)
        ) ?? throw new InvalidOperationException("Employee directory function returned no result.");
        return new EmployeeDirectoryEntry(
            MapEmployeeState(row.Status),
            row.EmployeeId,
            row.MembershipId,
            row.UserId);
    }

    /// <summary>Linha de retorno: <c>sessions_valid_after timestamp with time zone</c> (0 ou 1 linha).</summary>
    private sealed record SessionLockRow(DateTimeOffset? SessionsValidAfter);

    /// <summary>
    /// Executa a consulta restaurando o contrato de exceção do acesso direto ao Npgsql:
    /// o EF envolve falhas do provedor em <see cref="InvalidOperationException"/>
    /// ("transient failure"), o que cega <c>catch (PostgresException)</c> dos serviços
    /// e políticas de retry baseadas em <c>NpgsqlException.IsTransient</c>. A exceção
    /// original do provedor é relançada com a pilha preservada.
    /// </summary>
    private static async Task<T> UnwrapProviderExceptionAsync<T>(Task<T> query)
    {
        try
        {
            return await query;
        }
        catch (InvalidOperationException exception)
            when (exception.InnerException is PostgresException)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw; // Inalcançável: Capture.Throw acima sempre lança.
        }
    }

    /// <summary>
    /// Linha de retorno: <c>status text, provider_pending_at timestamptz, transitioned boolean</c>.
    /// </summary>
    private sealed record OwnerInvitationDeliveryIngressRow(
        string Status,
        DateTimeOffset? ProviderPendingAt,
        bool Transitioned);

    /// <summary>Linha de retorno: <c>status text, attempt_id uuid, owner_email text, claimed boolean</c>.</summary>
    private sealed record OwnerInvitationProviderClaimRow(
        string Status,
        Guid? AttemptId,
        string? OwnerEmail,
        bool Claimed);

    /// <summary>Linha de retorno: <c>status text, finalized boolean</c>.</summary>
    private sealed record OwnerInvitationProviderFinalizationRow(string Status, bool Finalized);

    /// <summary>
    /// Linha de retorno: <c>status text, group_id uuid, company_id uuid, owner_user_id uuid,
    /// owner_membership_id uuid, owner_invitation_id uuid</c>.
    /// </summary>
    private sealed record ProvisionedOrganizationRow(
        string Status,
        Guid GroupId,
        Guid CompanyId,
        Guid OwnerUserId,
        Guid OwnerMembershipId,
        Guid OwnerInvitationId);

    /// <summary>Linha de retorno: <c>status text, employee_id uuid, membership_id uuid, user_id uuid</c>.</summary>
    private sealed record EmployeeDirectoryEntryRow(
        string Status,
        Guid EmployeeId,
        Guid MembershipId,
        Guid UserId);

    private static OwnerInvitationDeliveryIngressResult ToIngressResult(OwnerInvitationDeliveryIngressRow? row)
    {
        if (row is null || string.Equals(row.Status, "not_found", StringComparison.Ordinal))
        {
            throw new OwnerInvitationIngressPermanentException();
        }

        return new OwnerInvitationDeliveryIngressResult(row.Status, row.ProviderPendingAt, row.Transitioned);
    }

    private static OwnerInvitationProviderClaim ToProviderClaim(OwnerInvitationProviderClaimRow? row)
    {
        if (row is null || string.Equals(row.Status, "not_found", StringComparison.Ordinal))
        {
            throw new OwnerInvitationIngressPermanentException();
        }

        return new OwnerInvitationProviderClaim(row.Status, row.AttemptId, row.OwnerEmail, row.Claimed);
    }

    private static OwnerInvitationProviderFinalization ToProviderFinalization(OwnerInvitationProviderFinalizationRow? row)
    {
        if (row is null || string.Equals(row.Status, "not_found", StringComparison.Ordinal))
        {
            throw new OwnerInvitationIngressPermanentException();
        }

        return new OwnerInvitationProviderFinalization(row.Status, row.Finalized);
    }

    private static PlatformOrganizationProvisioningState MapProvisioningState(string status) => status switch
    {
        "created" => PlatformOrganizationProvisioningState.Created,
        "replay" => PlatformOrganizationProvisioningState.Replay,
        "idempotency_conflict" => PlatformOrganizationProvisioningState.IdempotencyConflict,
        _ => throw new InvalidOperationException("Organization provisioning returned an unknown state."),
    };

    private static EmployeeDirectoryWriteState MapEmployeeState(string status) => status switch
    {
        "created" => EmployeeDirectoryWriteState.Created,
        "replay" => EmployeeDirectoryWriteState.Replay,
        "idempotency_conflict" => EmployeeDirectoryWriteState.IdempotencyConflict,
        _ => throw new InvalidOperationException("Employee directory function returned unknown status."),
    };
}

internal sealed record LockedUser(bool Exists, DateTimeOffset? SessionsValidAfter);

internal sealed record OwnerInvitationProviderClaim(
    string Status,
    Guid? AttemptId,
    string? OwnerEmail,
    bool Claimed);

internal sealed record OwnerInvitationProviderFinalization(string Status, bool Finalized);

internal sealed record ProvisionedOrganization(
    PlatformOrganizationProvisioningState State,
    Guid GroupId,
    Guid CompanyId,
    Guid OwnerUserId,
    Guid OwnerMembershipId,
    Guid OwnerInvitationId);

internal sealed record EmployeeDirectoryEntry(
    EmployeeDirectoryWriteState State,
    Guid EmployeeId,
    Guid MembershipId,
    Guid UserId);
