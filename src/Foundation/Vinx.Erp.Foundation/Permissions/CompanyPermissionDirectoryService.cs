using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Permissions;

public sealed record CompanyMemberRoleView(Guid RoleId, string Name);

public sealed record CompanyMemberPermissionsView(
    Guid MembershipId,
    string DisplayName,
    IReadOnlyList<CompanyMemberRoleView> Roles,
    IReadOnlyList<string> Capabilities,
    string? InvitationStatus);

public sealed record CompanyRolePermissionsView(
    Guid RoleId,
    string Name,
    IReadOnlyList<string> Capabilities);

public enum CompanyPermissionDirectoryReadState
{
    Succeeded,
    PermissionDenied,
    AuditUnavailable,
}

public sealed record CompanyPermissionDirectoryResult<T>(
    CompanyPermissionDirectoryReadState State,
    IReadOnlyList<T>? Items = null);

/// <summary>
/// Leitura do diretório de permissões da empresa (membros com papéis e papéis com
/// grants) por funções SQL com gate de capability no banco; negações do gate
/// (SQLSTATE estável, sem casar mensagem) geram auditoria <c>Denied</c> antes
/// do 403, sem oráculo de existência de empresa.
/// </summary>
public sealed class CompanyPermissionDirectoryService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    ILogger<CompanyPermissionDirectoryService> logger
)
{
    private const string Module = "foundation";
    private const string MembersListOperation = "foundation.permissions.members.list";
    private const string RolesListOperation = "foundation.permissions.roles.list";
    private const string DenialReason = "capability_denied";

    public Task<CompanyPermissionDirectoryResult<CompanyMemberPermissionsView>> ListMembersAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    ) =>
        ExecuteListAsync(
            actor,
            companyId,
            MembersListOperation,
            "company_member",
            (context, company, token) => context.ListCompanyMembersWithRolesAsync(company, token),
            member => new CompanyMemberPermissionsView(
                member.MembershipId,
                member.DisplayName,
                member
                    .RoleIds.Zip(
                        member.RoleNames,
                        (roleId, name) => new CompanyMemberRoleView(roleId, name)
                    )
                    .ToList(),
                member.CapabilityNames,
                member.InvitationStatus),
            cancellationToken
        );

    public Task<CompanyPermissionDirectoryResult<CompanyRolePermissionsView>> ListRolesAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    ) =>
        ExecuteListAsync(
            actor,
            companyId,
            RolesListOperation,
            "company_role",
            (context, company, token) => context.ListCompanyRolesWithGrantsAsync(company, token),
            role => new CompanyRolePermissionsView(role.RoleId, role.Name, role.CapabilityNames),
            cancellationToken
        );

    private async Task<CompanyPermissionDirectoryResult<TView>> ExecuteListAsync<TRow, TView>(
        ActorContext actor,
        Guid companyId,
        string operation,
        string resourceType,
        Func<FoundationDbContext, Guid, CancellationToken, Task<IReadOnlyList<TRow>>> read,
        Func<TRow, TView> project,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await FoundationRlsContext.ApplyCommandAsync(database.Database, actor, companyId, cancellationToken);
            var rows = await read(database, companyId, cancellationToken);
            var items = rows.Select(project).ToList();
            // Leitura concluída: descarta o contexto RLS transacional sem confirmar escrita.
            await transaction.RollbackAsync(cancellationToken);
            return new CompanyPermissionDirectoryResult<TView>(
                CompanyPermissionDirectoryReadState.Succeeded,
                items
            );
        }
        catch (PostgresException exception)
            when (exception.SqlState == PermissionsSqlStates.CapabilityDenied)
        {
            return await ToAuditedDenialAsync<TView>(actor, companyId, operation, resourceType, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Company permission directory {Operation} {Module} {Result} {Reason}",
                operation,
                Module,
                "unavailable",
                "database_or_audit_unavailable"
            );
            return new CompanyPermissionDirectoryResult<TView>(
                CompanyPermissionDirectoryReadState.AuditUnavailable
            );
        }
    }

    private async Task<CompanyPermissionDirectoryResult<T>> ToAuditedDenialAsync<T>(
        ActorContext actor,
        Guid companyId,
        string operation,
        string resourceType,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await FoundationRlsContext.ApplyCommandAsync(database.Database, actor, companyId, cancellationToken);
            await auditAppender.AppendAsync(
                new AuditEventData(
                    actor.UserId,
                    AuditScope.Company,
                    companyId,
                    AuditSource.Web,
                    operation,
                    resourceType,
                    null,
                    AuditOutcome.Denied,
                    new CorrelationContext(actor.CorrelationId).CorrelationId,
                    DenialReason,
                    "{}"
                ),
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            return new CompanyPermissionDirectoryResult<T>(CompanyPermissionDirectoryReadState.PermissionDenied);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Company permission directory {Operation} {Module} {Result} {Reason}",
                operation,
                Module,
                "unavailable",
                "denial_audit_unavailable"
            );
            return new CompanyPermissionDirectoryResult<T>(CompanyPermissionDirectoryReadState.AuditUnavailable);
        }
    }
}
