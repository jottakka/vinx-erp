using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;
using Wolverine;
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
            result = await ExecuteAsync(normalized, cancellationToken);
            if (result.State is PlatformOrganizationProvisioningState.Created)
            {
                outbox.Enroll(database);
                await outbox.PublishAsync(
                    new OwnerInvitationDeliveryRequested(result.OwnerInvitationId!.Value),
                    new DeliveryOptions { CorrelationId = actor.CorrelationId.ToString("D") }
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
        CancellationToken cancellationToken
    )
    {
        var created = await database.ProvisionPlatformOrganizationAsync(
            command.RequestId,
            command.GroupName,
            command.CompanyName,
            command.OwnerDisplayName,
            command.OwnerEmail,
            command.PayloadHash,
            cancellationToken
        );
        var result = new PlatformOrganizationProvisioningResult(
            created.State,
            created.GroupId,
            created.CompanyId,
            created.OwnerUserId,
            created.OwnerMembershipId,
            created.OwnerInvitationId
        );

        if (result.State is PlatformOrganizationProvisioningState.Created)
        {
            await database.EnsurePlatformOwnerEmployeeCreateAsync(
                command.RequestId,
                result.CompanyId!.Value,
                result.OwnerMembershipId!.Value,
                cancellationToken
            );
            await database.EnsureCompanyDefaultRolesAsync(
                result.CompanyId!.Value,
                cancellationToken
            );
        }

        return result;
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
