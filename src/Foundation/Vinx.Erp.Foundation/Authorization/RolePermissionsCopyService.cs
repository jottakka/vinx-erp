using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Configurations;

namespace Vinx.Erp.Foundation.Authorization;

/// <summary>
/// Copia os papéis vivos da membership fonte para a destino com semântica de
/// substituição, em uma única transação com advisory lock por empresa e bloqueio
/// de último administrador (o estado final não pode deixar a empresa sem
/// administrador de papéis). Conjuntos idênticos são sucesso sem mutação;
/// fonte igual ao destino é contrato inválido.
/// </summary>
public sealed class RolePermissionsCopyService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    AuthorizationTelemetry telemetry,
    CapabilityEvaluator capabilityEvaluator)
{
    private const string CommandName = "foundation.roles.copy";
    private const string ReplacementReason = "role_replaced_by_permission_copy";
    private static readonly CapabilityRequirement RequiredCapability =
        new(KnownCapabilities.RolesAssign, CapabilityScope.Company);

    public async Task<RolePermissionsCopyResult> CopyAsync(
        ActorContext actor,
        CopyRolePermissionsCommand command,
        CancellationToken cancellationToken)
    {
        if (command.SourceMembershipId == command.TargetMembershipId)
        {
            return RolePermissionsCopyResult.Invalid();
        }

        var payloadHash = ComputePayloadHash(command);
        AuthorizationErrorCode? denial = null;

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            denial = await EvaluateAuthorizationAsync(actor, command.CompanyId, cancellationToken);
            if (denial is null)
            {
                // Serializa rebaixamentos concorrentes de administrador na mesma empresa
                // antes de qualquer leitura de estado ou preparo de mutação.
                await database.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({command.CompanyId.ToString()}, 0))",
                    cancellationToken);
                var existing = await database.IdempotencyRecords.SingleOrDefaultAsync(record =>
                    record.CompanyId == command.CompanyId
                    && record.ActorUserId == actor.UserId
                    && record.CommandName == CommandName
                    && record.RequestId == command.RequestId,
                    cancellationToken);
                if (existing is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                    {
                        return RolePermissionsCopyResult.From(await ReturnAuditedDenialAsync(
                            actor,
                            command.CompanyId,
                            AuthorizationErrorCode.IdempotencyConflict,
                            cancellationToken));
                    }

                    var replay = JsonSerializer.Deserialize<CopyRolePermissionsResult>(existing.ResultJson);
                    return replay is null
                        ? RolePermissionsCopyResult.From(AuditUnavailable(AuditUnavailablePhase.IdempotencyReplay))
                        : RolePermissionsCopyResult.From(AuthorizationResult<CopyRolePermissionsResult>.Success(replay));
                }

                // O query filter global já restringe às memberships e atribuições vivas.
                var sourceMembership = await database.Memberships.SingleOrDefaultAsync(candidate =>
                    candidate.CompanyId == command.CompanyId && candidate.Id == command.SourceMembershipId,
                    cancellationToken);
                var targetMembership = await database.Memberships.SingleOrDefaultAsync(candidate =>
                    candidate.CompanyId == command.CompanyId && candidate.Id == command.TargetMembershipId,
                    cancellationToken);
                if (sourceMembership is null || targetMembership is null)
                {
                    denial = AuthorizationErrorCode.ResourceNotFound;
                }
                else
                {
                    var incomingRoleIds = (await database.RoleAssignments
                        .Where(assignment =>
                            assignment.CompanyId == command.CompanyId
                            && assignment.MembershipId == command.SourceMembershipId)
                        .Select(assignment => assignment.RoleId)
                        .ToListAsync(cancellationToken))
                        .Distinct()
                        .OrderBy(roleId => roleId)
                        .ToList();
                    var targetAssignments = await database.RoleAssignments
                        .Where(assignment =>
                            assignment.CompanyId == command.CompanyId
                            && assignment.MembershipId == command.TargetMembershipId)
                        .ToListAsync(cancellationToken);
                    var currentRoleIds = targetAssignments
                        .Select(assignment => assignment.RoleId)
                        .ToHashSet();

                    // Estado final: alvo com o conjunto da fonte; sem outros administradores
                    // e sem grant no conjunto de entrada, a empresa ficaria sem administrador.
                    if (
                        !await LastAdminGuard.CompanyRetainsRolesAssignAdminAsync(
                            database, command.CompanyId, command.TargetMembershipId, cancellationToken)
                        && !await LastAdminGuard.MembershipRetainsRolesAssignAdminAsync(
                            database, command.CompanyId, command.SourceMembershipId, null, cancellationToken))
                    {
                        denial = AuthorizationErrorCode.LastAdminGuard;
                    }
                    else
                    {
                        var setsAreIdentical = currentRoleIds.SetEquals(incomingRoleIds);
                        if (!setsAreIdentical)
                        {
                            foreach (var assignment in targetAssignments.Where(assignment =>
                                !incomingRoleIds.Contains(assignment.RoleId)))
                            {
                                assignment.Revoke(ReplacementReason, actor.UserId, command.RequestId);
                            }

                            foreach (var roleId in incomingRoleIds.Where(roleId => !currentRoleIds.Contains(roleId)))
                            {
                                database.RoleAssignments.Add(new RoleAssignment(
                                    Guid.NewGuid(),
                                    command.CompanyId,
                                    roleId,
                                    command.TargetMembershipId,
                                    actor.UserId,
                                    command.RequestId));
                            }
                        }

                        var result = new CopyRolePermissionsResult(command.TargetMembershipId, incomingRoleIds);
                        database.IdempotencyRecords.Add(new IdempotencyRecord(
                            Guid.NewGuid(),
                            command.CompanyId,
                            actor.UserId,
                            CommandName,
                            command.RequestId,
                            payloadHash,
                            JsonSerializer.Serialize(result)));
                        await auditAppender.AppendAsync(new AuditEventData(
                            actor.UserId,
                            AuditScope.Company,
                            command.CompanyId,
                            AuditSource.Web,
                            CommandName,
                            "membership",
                            command.TargetMembershipId,
                            AuditOutcome.Success,
                            new CorrelationContext(actor.CorrelationId).CorrelationId,
                            null,
                            "{}"), cancellationToken);
                        await database.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        return RolePermissionsCopyResult.From(AuthorizationResult<CopyRolePermissionsResult>.Success(result));
                    }
                }
            }

            await transaction.RollbackAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyConflict(exception))
        {
            database.ChangeTracker.Clear();
            return RolePermissionsCopyResult.From(
                await ReturnConcurrentReplayAsync(actor, command, payloadHash, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            database.ChangeTracker.Clear();
            return RolePermissionsCopyResult.From(AuditUnavailable(AuditUnavailablePhase.SuccessTransaction));
        }

        return RolePermissionsCopyResult.From(
            await ReturnAuditedDenialAsync(actor, command.CompanyId, denial!.Value, cancellationToken));
    }

    private async Task<AuthorizationResult<CopyRolePermissionsResult>> ReturnAuditedDenialAsync(
        ActorContext actor,
        Guid companyId,
        AuthorizationErrorCode denial,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, companyId, cancellationToken);
            await auditAppender.AppendAsync(new AuditEventData(
                actor.UserId,
                AuditScope.Company,
                companyId,
                AuditSource.Web,
                denial.ToStableCode(),
                "membership",
                null,
                AuditOutcome.Denied,
                new CorrelationContext(actor.CorrelationId).CorrelationId,
                denial.ToStableCode(),
                "{}"), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AuthorizationResult<CopyRolePermissionsResult>.Failure(denial);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return AuditUnavailable(AuditUnavailablePhase.DenialAudit);
        }
    }

    private async Task<AuthorizationResult<CopyRolePermissionsResult>> ReturnConcurrentReplayAsync(
        ActorContext actor,
        CopyRolePermissionsCommand command,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            var record = await database.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(candidate =>
                candidate.CompanyId == command.CompanyId
                && candidate.ActorUserId == actor.UserId
                && candidate.CommandName == CommandName
                && candidate.RequestId == command.RequestId,
                cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            if (record is null)
            {
                return await ReturnAuditedDenialAsync(
                    actor,
                    command.CompanyId,
                    AuthorizationErrorCode.IntegrityConflict,
                    cancellationToken);
            }

            if (!string.Equals(record.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return await ReturnAuditedDenialAsync(actor, command.CompanyId, AuthorizationErrorCode.IdempotencyConflict, cancellationToken);
            }

            var replay = JsonSerializer.Deserialize<CopyRolePermissionsResult>(record.ResultJson);
            return replay is null
                ? AuditUnavailable(AuditUnavailablePhase.ConcurrentReplay)
                : AuthorizationResult<CopyRolePermissionsResult>.Success(replay);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return AuditUnavailable(AuditUnavailablePhase.ConcurrentReplay);
        }
    }

    private async Task<AuthorizationErrorCode?> EvaluateAuthorizationAsync(
        ActorContext actor,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        var actorMembership = await database.Memberships.SingleOrDefaultAsync(membership =>
            membership.CompanyId == companyId && membership.UserId == actor.UserId,
            cancellationToken);
        if (actorMembership is null)
        {
            return AuthorizationErrorCode.MembershipRequired;
        }

        if (await capabilityEvaluator.HasGrantAsync(
                companyId,
                actorMembership.Id,
                RequiredCapability,
                cancellationToken))
        {
            return null;
        }

        return AuthorizationErrorCode.CapabilityDenied;
    }

    private static bool IsIdempotencyKeyConflict(DbUpdateException exception)
    {
        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException postgresException
                && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(
                    postgresException.ConstraintName,
                    IdempotencyRecordConfiguration.KeyIndexDatabaseName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private AuthorizationResult<CopyRolePermissionsResult> AuditUnavailable(AuditUnavailablePhase phase)
    {
        telemetry.RecordAuditUnavailable(AuthorizationOperation.RolePermissionsCopy, phase);
        return AuthorizationResult<CopyRolePermissionsResult>.Failure(AuthorizationErrorCode.AuditUnavailable);
    }

    private Task ApplyContextAsync(ActorContext actor, Guid companyId, CancellationToken cancellationToken) =>
        FoundationRlsContext.ApplyCommandAsync(database.Database, actor, companyId, cancellationToken);

    private static string ComputePayloadHash(CopyRolePermissionsCommand command)
    {
        var payload = string.Concat(
            command.CompanyId.ToString("N"),
            "|",
            command.SourceMembershipId.ToString("N"),
            "|",
            command.TargetMembershipId.ToString("N"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
