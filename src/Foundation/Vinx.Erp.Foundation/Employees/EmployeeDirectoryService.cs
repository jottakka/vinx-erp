using System.ComponentModel.DataAnnotations;
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

namespace Vinx.Erp.Foundation.Employees;

public sealed record CreateEmployeeCommand(
    Guid RequestId,
    Guid CompanyId,
    string DisplayName,
    [property: EmailAddress] string Email
);

public enum EmployeeDirectoryWriteState
{
    Created,
    Replay,
    IdempotencyConflict,
    Forbidden,
    Conflict,
    Invalid,
    Unavailable,
}

public sealed record EmployeeDirectoryWriteResult(
    EmployeeDirectoryWriteState State,
    Guid? EmployeeId = null,
    Guid? MembershipId = null,
    Guid? UserId = null
);

public sealed record EmployeeDirectoryItem(Guid EmployeeId, Guid MembershipId, string DisplayName);

/// <summary>Reserva funcionários locais por uma única função transacional com autorização no banco.</summary>
public sealed class EmployeeDirectoryService(
    FoundationDbContext database,
    AuthorizedCompanySetResolver authorizedCompanies,
    AuditAppender auditAppender,
    ILogger<EmployeeDirectoryService> logger
)
{
    private static readonly CapabilityRequirement CreateRequirement = new(
        KnownCapabilities.EmployeesCreate,
        CapabilityScope.Company
    );

    private static readonly CapabilityRequirement ReadRequirement = new(
        KnownCapabilities.DirectoryRead,
        CapabilityScope.Company
    );

    public async Task<EmployeeDirectoryWriteResult> CreateAsync(
        ActorContext actor,
        CreateEmployeeCommand command,
        CancellationToken cancellationToken
    )
    {
        if (!TryNormalize(command, out var normalized))
            return new(EmployeeDirectoryWriteState.Invalid);
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
            var created = await database.CreateEmployeeDirectoryEntryAsync(
                normalized.RequestId,
                normalized.CompanyId,
                normalized.DisplayName,
                normalized.Email,
                normalized.PayloadHash,
                cancellationToken
            );
            var result = new EmployeeDirectoryWriteResult(
                created.State,
                created.EmployeeId,
                created.MembershipId,
                created.UserId
            );
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege
                && exception.MessageText == "employee directory capability is required"
            )
        {
            return await AuditDeniedAsync(actor, normalized.CompanyId, cancellationToken)
                ? new(EmployeeDirectoryWriteState.Forbidden)
                : new(EmployeeDirectoryWriteState.Unavailable);
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.UniqueViolation
                && exception.ConstraintName == "ix_users_email"
            )
        {
            return new(EmployeeDirectoryWriteState.Conflict);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Employee directory creation {Operation} {Module} {Result} {Reason}",
                "employee_directory_create",
                "foundation",
                "unavailable",
                "database_or_audit_unavailable"
            );
            return new(EmployeeDirectoryWriteState.Unavailable);
        }
    }

    public async Task<AuthorizationResult<IReadOnlyList<EmployeeDirectoryItem>>> ListAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    )
    {
        var authorization = await authorizedCompanies.ResolveAsync(
            actor,
            CreateRequirement,
            cancellationToken
        );
        if (!authorization.IsSuccess)
            return AuthorizationResult<IReadOnlyList<EmployeeDirectoryItem>>.Failure(
                authorization.ErrorCode!.Value
            );
        if (!authorization.Value!.CompanyIds.Contains(companyId))
            return AuthorizationResult<IReadOnlyList<EmployeeDirectoryItem>>.Failure(
                AuthorizationErrorCode.CapabilityDenied
            );
        return await authorizedCompanies.ExecuteReadAsync(
            actor,
            CreateRequirement,
            async (context, companyIds, token) =>
                (IReadOnlyList<EmployeeDirectoryItem>)
                    await context
                        .Employees.AsNoTracking()
                        .Where(x => x.CompanyId == companyId)
                        .OrderBy(x => x.DisplayName)
                        .Select(x => new EmployeeDirectoryItem(x.Id, x.MembershipId, x.DisplayName))
                        .ToListAsync(token),
            cancellationToken
        );
    }

    /// <summary>
    /// Lista opções de pessoas da empresa para pickers (nome e IDs opacos),
    /// autorizada pela capability de leitura <c>directory.read</c>; nunca
    /// reutiliza a capability de criação de funcionário.
    /// </summary>
    public async Task<AuthorizationResult<IReadOnlyList<EmployeeDirectoryItem>>> ListForPickerAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    )
    {
        var authorization = await authorizedCompanies.ResolveAsync(
            actor,
            ReadRequirement,
            cancellationToken
        );
        if (!authorization.IsSuccess)
            return AuthorizationResult<IReadOnlyList<EmployeeDirectoryItem>>.Failure(
                authorization.ErrorCode!.Value
            );
        if (!authorization.Value!.CompanyIds.Contains(companyId))
            return AuthorizationResult<IReadOnlyList<EmployeeDirectoryItem>>.Failure(
                AuthorizationErrorCode.CapabilityDenied
            );
        return await authorizedCompanies.ExecuteReadAsync(
            actor,
            ReadRequirement,
            async (context, companyIds, token) =>
                (IReadOnlyList<EmployeeDirectoryItem>)
                    await context
                        .Employees.AsNoTracking()
                        .Where(x => x.CompanyId == companyId)
                        .OrderBy(x => x.DisplayName)
                        .Select(x => new EmployeeDirectoryItem(x.Id, x.MembershipId, x.DisplayName))
                        .ToListAsync(token),
            cancellationToken
        );
    }

    private async Task<bool> AuditDeniedAsync(        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
            await FoundationRlsContext.ApplyCompanyAsync(
                database.Database,
                companyId,
                cancellationToken
            );
            await auditAppender.AppendAsync(
                new AuditEventData(
                    actor.UserId,
                    AuditScope.Company,
                    companyId,
                    AuditSource.Web,
                    "foundation.employee.create",
                    "employee",
                    null,
                    AuditOutcome.Denied,
                    new CorrelationContext(actor.CorrelationId).CorrelationId,
                    "capability_denied",
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
                "Employee directory creation {Operation} {Module} {Result} {Reason}",
                "employee_directory_create",
                "foundation",
                "unavailable",
                "denial_audit_unavailable"
            );
            return false;
        }
    }

    private static bool TryNormalize(CreateEmployeeCommand command, out Normalized normalized)
    {
        normalized = default!;
        var name = command.DisplayName?.Trim();
        var email = command.Email?.Trim().ToLowerInvariant();
        if (
            command.RequestId == Guid.Empty
            || command.CompanyId == Guid.Empty
            || string.IsNullOrWhiteSpace(name)
            || name.Length > 200
            || string.IsNullOrWhiteSpace(email)
            || email.Length > 320
        )
            return false;
        try
        {
            if (!string.Equals(new MailAddress(email).Address, email, StringComparison.Ordinal))
                return false;
        }
        catch (FormatException)
        {
            return false;
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new[] { command.RequestId.ToString("D"), command.CompanyId.ToString("D"), name, email }
        );
        normalized = new(
            command.RequestId,
            command.CompanyId,
            name,
            email,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()
        );
        return true;
    }

    private sealed record Normalized(
        Guid RequestId,
        Guid CompanyId,
        string DisplayName,
        string Email,
        string PayloadHash
    );
}
