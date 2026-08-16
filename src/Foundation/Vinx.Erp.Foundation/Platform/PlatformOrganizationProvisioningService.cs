using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;
using Wolverine.EntityFrameworkCore;

namespace Vinx.Erp.Foundation.Platform;

public sealed record PlatformOrganizationProvisioningCommand(
    Guid RequestId,
    string GroupName,
    string CompanyName,
    string OwnerDisplayName,
    string OwnerEmail
);

public enum PlatformOrganizationProvisioningState
{
    Created,
    Replay,
    IdempotencyConflict,
    Conflict,
    Forbidden,
    Invalid,
    Unavailable,
}

public sealed record PlatformOrganizationProvisioningResult(
    PlatformOrganizationProvisioningState State,
    Guid? GroupId = null,
    Guid? CompanyId = null,
    Guid? OwnerUserId = null,
    Guid? OwnerMembershipId = null,
    Guid? OwnerInvitationId = null
);

/// <summary>
/// Chama a única função transacional que cria a organização inicial e o dono pendente.
/// </summary>
public sealed class PlatformOrganizationProvisioningService(
    FoundationDbContext database,
    IDbContextOutbox outbox,
    ILogger<PlatformOrganizationProvisioningService> logger
)
{
    private const string Operation = "platform_organization_provisioning";
    private const string Module = "foundation";

    public async Task<PlatformOrganizationProvisioningResult> ProvisionAsync(
        ActorContext actor,
        PlatformOrganizationProvisioningCommand command,
        CancellationToken cancellationToken
    )
    {
        if (!TryNormalize(command, out var normalized))
        {
            return new PlatformOrganizationProvisioningResult(
                PlatformOrganizationProvisioningState.Invalid
            );
        }

        var commitAttempted = false;
        PlatformOrganizationProvisioningResult? result = null;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                cancellationToken
            );
            await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
            result = await ExecuteAsync(normalized, transaction, cancellationToken);
            if (result.State is PlatformOrganizationProvisioningState.Created)
            {
                outbox.Enroll(database);
                await outbox.PublishAsync(
                    new OwnerInvitationDeliveryRequested(result.OwnerInvitationId!.Value)
                );
            }

            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
            when (!commitAttempted && IsExpectedAccessDenial(exception))
        {
            return new PlatformOrganizationProvisioningResult(
                PlatformOrganizationProvisioningState.Forbidden
            );
        }
        catch (PostgresException exception)
            when (!commitAttempted && IsExpectedResourceConflict(exception))
        {
            return new PlatformOrganizationProvisioningResult(
                PlatformOrganizationProvisioningState.Conflict
            );
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            if (commitAttempted)
            {
                logger.LogError(
                    exception,
                    "Platform organization provisioning {Operation} {Module} {Result} {Reason}",
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
                "Platform organization provisioning {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "unavailable",
                commitAttempted ? "outbox_commit_outcome_unknown" : "database_or_audit_unavailable"
            );
            return new PlatformOrganizationProvisioningResult(
                PlatformOrganizationProvisioningState.Unavailable
            );
        }

        if (result.State is PlatformOrganizationProvisioningState.Created)
        {
            try
            {
                await outbox.FlushOutgoingMessagesAsync();
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Platform organization provisioning {Operation} {Module} {Result} {Reason}",
                    Operation,
                    Module,
                    "created",
                    "outbox_flush_deferred"
                );
            }
        }

        return result;
    }

    private async Task<PlatformOrganizationProvisioningResult> ExecuteAsync(
        NormalizedCommand command,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        var connection = database.Database.GetDbConnection();
        await using var sql = connection.CreateCommand();
        sql.Transaction = transaction.GetDbTransaction();
        sql.CommandText = """
            SELECT status, group_id, company_id, owner_user_id, owner_membership_id, owner_invitation_id
            FROM foundation.provision_platform_organization(
                @request_id, @group_name, @company_name, @owner_display_name, @owner_email, @payload_hash)
            """;
        AddParameter(sql, "request_id", command.RequestId);
        AddParameter(sql, "group_name", command.GroupName);
        AddParameter(sql, "company_name", command.CompanyName);
        AddParameter(sql, "owner_display_name", command.OwnerDisplayName);
        AddParameter(sql, "owner_email", command.OwnerEmail);
        AddParameter(sql, "payload_hash", command.PayloadHash);
        PlatformOrganizationProvisioningResult result;
        await using (var reader = await sql.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "Organization provisioning did not return a result."
                );
            }

            var state = reader.GetString(0) switch
            {
                "created" => PlatformOrganizationProvisioningState.Created,
                "replay" => PlatformOrganizationProvisioningState.Replay,
                "idempotency_conflict" => PlatformOrganizationProvisioningState.IdempotencyConflict,
                _ => throw new InvalidOperationException(
                    "Organization provisioning returned an unknown state."
                ),
            };
            result = new PlatformOrganizationProvisioningResult(
                state,
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetGuid(4),
                reader.GetGuid(5)
            );
        }

        if (result.State is PlatformOrganizationProvisioningState.Created)
        {
            await EnsureInitialOwnerCanCreateEmployeesAsync(
                connection,
                transaction,
                command.RequestId,
                result.CompanyId!.Value,
                result.OwnerMembershipId!.Value,
                cancellationToken
            );
        }

        return result;
    }

    /// <summary>
    /// Mantém a reserva local de funcionário disponível para o dono inicial
    /// sem conceder qualquer acesso de tenant ao operador global.
    /// </summary>
    private static async Task EnsureInitialOwnerCanCreateEmployeesAsync(
        DbConnection connection,
        IDbContextTransaction transaction,
        Guid requestId,
        Guid companyId,
        Guid ownerMembershipId,
        CancellationToken cancellationToken
    )
    {
        await using var sql = connection.CreateCommand();
        sql.Transaction = transaction.GetDbTransaction();
        sql.CommandText =
            "SELECT foundation.ensure_platform_owner_employee_create(@request_id, @company_id, @owner_membership_id)";
        AddParameter(sql, "request_id", requestId);
        AddParameter(sql, "company_id", companyId);
        AddParameter(sql, "owner_membership_id", ownerMembershipId);
        await sql.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool IsExpectedAccessDenial(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.InsufficientPrivilege
        && exception.MessageText is "platform operator is required" or "mfa assurance is required";

    private static bool IsExpectedResourceConflict(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.UniqueViolation
        && exception.ConstraintName
            is "ix_groups_name"
                or "ix_companies_normalized_name"
                or "ix_users_email";

    private static bool TryNormalize(
        PlatformOrganizationProvisioningCommand command,
        out NormalizedCommand normalized
    )
    {
        normalized = default!;
        if (command.RequestId == Guid.Empty)
        {
            return false;
        }

        var groupName = command.GroupName?.Trim();
        var companyName = command.CompanyName?.Trim();
        var ownerDisplayName = command.OwnerDisplayName?.Trim();
        var ownerEmail = command.OwnerEmail?.Trim().ToLowerInvariant();
        if (
            string.IsNullOrWhiteSpace(groupName)
            || groupName.Length > 200
            || string.IsNullOrWhiteSpace(companyName)
            || companyName.Length > 200
            || string.IsNullOrWhiteSpace(ownerDisplayName)
            || ownerDisplayName.Length > 200
            || string.IsNullOrWhiteSpace(ownerEmail)
            || ownerEmail.Length > 320
        )
        {
            return false;
        }

        var canonicalPayload = JsonSerializer.SerializeToUtf8Bytes(
            new[]
            {
                command.RequestId.ToString("D"),
                groupName,
                companyName,
                ownerDisplayName,
                ownerEmail,
            }
        );
        var payloadHash = Convert.ToHexString(SHA256.HashData(canonicalPayload)).ToLowerInvariant();
        normalized = new NormalizedCommand(
            command.RequestId,
            groupName,
            companyName,
            ownerDisplayName,
            ownerEmail,
            payloadHash
        );
        return true;
    }

    private sealed record NormalizedCommand(
        Guid RequestId,
        string GroupName,
        string CompanyName,
        string OwnerDisplayName,
        string OwnerEmail,
        string PayloadHash
    );
}
