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

namespace Vinx.Erp.Foundation.Platform;

public sealed record PlatformOrganizationProvisioningCommand(
    Guid RequestId,
    string GroupName,
    string CompanyName,
    string OwnerDisplayName,
    string OwnerEmail);

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
    Guid? OwnerInvitationId = null);

/// <summary>
/// Chama a única função transacional que cria a organização inicial e o dono pendente.
/// </summary>
public sealed class PlatformOrganizationProvisioningService(
    FoundationDbContext database,
    ILogger<PlatformOrganizationProvisioningService> logger)
{
    private const string Operation = "platform_organization_provisioning";
    private const string Module = "foundation";

    public async Task<PlatformOrganizationProvisioningResult> ProvisionAsync(
        ActorContext actor,
        PlatformOrganizationProvisioningCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryNormalize(command, out var normalized))
        {
            return new PlatformOrganizationProvisioningResult(PlatformOrganizationProvisioningState.Invalid);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
            var result = await ExecuteAsync(actor, normalized, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (PostgresException exception) when (IsExpectedAccessDenial(exception))
        {
            return new PlatformOrganizationProvisioningResult(PlatformOrganizationProvisioningState.Forbidden);
        }
        catch (PostgresException exception) when (IsExpectedResourceConflict(exception))
        {
            return new PlatformOrganizationProvisioningResult(PlatformOrganizationProvisioningState.Conflict);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
                "database_or_audit_unavailable");
            return new PlatformOrganizationProvisioningResult(PlatformOrganizationProvisioningState.Unavailable);
        }
    }

    private async Task<PlatformOrganizationProvisioningResult> ExecuteAsync(
        ActorContext actor,
        NormalizedCommand command,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
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
        await using var reader = await sql.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Organization provisioning did not return a result.");
        }

        var state = reader.GetString(0) switch
        {
            "created" => PlatformOrganizationProvisioningState.Created,
            "replay" => PlatformOrganizationProvisioningState.Replay,
            "idempotency_conflict" => PlatformOrganizationProvisioningState.IdempotencyConflict,
            _ => throw new InvalidOperationException("Organization provisioning returned an unknown state."),
        };
        return new PlatformOrganizationProvisioningResult(
            state,
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            reader.GetGuid(5));
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
        && exception.ConstraintName is
            "ix_groups_name"
            or "ix_companies_normalized_name"
            or "ix_users_email";

    private static bool TryNormalize(
        PlatformOrganizationProvisioningCommand command,
        out NormalizedCommand normalized)
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
        if (string.IsNullOrWhiteSpace(groupName) || groupName.Length > 200
            || string.IsNullOrWhiteSpace(companyName) || companyName.Length > 200
            || string.IsNullOrWhiteSpace(ownerDisplayName) || ownerDisplayName.Length > 200
            || string.IsNullOrWhiteSpace(ownerEmail) || ownerEmail.Length > 320)
        {
            return false;
        }

        var canonicalPayload = JsonSerializer.SerializeToUtf8Bytes(
            new[] { command.RequestId.ToString("D"), groupName, companyName, ownerDisplayName, ownerEmail });
        var payloadHash = Convert.ToHexString(SHA256.HashData(canonicalPayload)).ToLowerInvariant();
        normalized = new NormalizedCommand(command.RequestId, groupName, companyName, ownerDisplayName, ownerEmail, payloadHash);
        return true;
    }

    private sealed record NormalizedCommand(
        Guid RequestId,
        string GroupName,
        string CompanyName,
        string OwnerDisplayName,
        string OwnerEmail,
        string PayloadHash);
}
