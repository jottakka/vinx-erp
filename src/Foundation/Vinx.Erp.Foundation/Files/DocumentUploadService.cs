using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Files;

public sealed class DocumentUploadService(
    FoundationDbContext database,
    FileStorage storage,
    IMalwareScanner malwareScanner,
    IOptions<MalwareScannerOptions> malwareScannerOptions,
    AuditAppender auditAppender,
    ILogger<DocumentUploadService> logger,
    CapabilityEvaluator capabilityEvaluator)
{
    private const string IntentCommandName = "foundation.files.upload.intent";
    private const string ConfirmationCommandName = "foundation.files.upload.confirm";
    private const string InspectionOperationName = "foundation.files.upload.inspect";
    private const string ScanOperationName = "foundation.files.upload.scan";
    private const string AuditOperationName = "foundation.files.upload.audit";
    private const string ScanModuleName = "files";
    private const string ScanEventTemplate =
        "Document malware scan {Operation} {Module} {Result} {Reason} after {ElapsedMs}ms";
    private static readonly CapabilityRequirement UploadCapability =
        new(KnownCapabilities.FilesUpload, CapabilityScope.Company);
    private static readonly CapabilityRequirement ConfirmCapability =
        new(KnownCapabilities.FilesConfirm, CapabilityScope.Company);

    // Somente falhas determinísticas são terminais; qualquer outra (scanner indisponível, timeout,
    // veredito malformado/antigo ou storage ilegível) é transitória e mantém a quarentena (retryable).
    private static readonly HashSet<string> TerminalVerificationFailures = new(StringComparer.Ordinal)
    {
        "malware_detected",
        "object_key_mismatch",
        "size_mismatch",
        "checksum_mismatch",
        "content_type_mismatch"
    };

    public async Task<DocumentUploadResult<DocumentUploadIntentResult>> CreateIntentAsync(
        ActorContext actor,
        CreateDocumentUploadIntentCommand command,
        CancellationToken cancellationToken)
    {
        var payloadHash = ComputeIntentPayloadHash(command);
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            var membership = await GetMembershipAsync(actor.UserId, command.CompanyId, cancellationToken);
            if (membership is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await ReturnAuditedDenialAsync<DocumentUploadIntentResult>(
                    actor, command.CompanyId, "membership_required", StatusCodes.Status403Forbidden, cancellationToken);
            }

            if (!await capabilityEvaluator.HasGrantAsync(command.CompanyId, membership.Id, UploadCapability, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return await ReturnAuditedDenialAsync<DocumentUploadIntentResult>(
                    actor, command.CompanyId, "capability_denied", StatusCodes.Status403Forbidden, cancellationToken);
            }

            var existing = await FindIdempotencyAsync(actor.UserId, command.CompanyId, IntentCommandName, command.RequestId, cancellationToken);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    return await ReturnAuditedDenialAsync<DocumentUploadIntentResult>(
                        actor, command.CompanyId, "idempotency_conflict", StatusCodes.Status409Conflict, cancellationToken);
                }

                return await CreateIntentReplayAsync(actor, command.CompanyId, existing.ResultJson, cancellationToken);
            }

            var validationError = DocumentUploadIntentCommandValidation.Validate(command);
            if (validationError is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await ReturnAuditedDenialAsync<DocumentUploadIntentResult>(
                    actor, command.CompanyId, validationError, StatusCodes.Status400BadRequest, cancellationToken);
            }

            var file = new FileRecord(
                Guid.NewGuid(),
                command.CompanyId,
                membership.Id,
                command.FileName,
                Path.GetExtension(command.FileName),
                command.SizeBytes,
                command.MimeType,
                command.Checksum,
                command.Classification,
                command.Origin,
                command.RetentionUntil,
                actor.UserId,
                command.RequestId);
            var storedResult = new StoredIntentResult(file.Id, file.ProcessingState, file.ProviderKey);
            database.Files.Add(file);
            database.IdempotencyRecords.Add(NewIdempotencyRecord(
                actor.UserId, command.CompanyId, IntentCommandName, command.RequestId, payloadHash, storedResult));
            await AppendAuditAsync(
                actor, command.CompanyId, IntentCommandName, "file", file.Id, AuditOutcome.Success, null, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await CreateIntentResultAsync(storedResult, cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            return await ReturnConcurrentIntentReplayAsync(actor, command, payloadHash, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnexpectedFailure(exception, IntentCommandName, "upload_intent_unavailable");
            return DocumentUploadResult<DocumentUploadIntentResult>.Failure("upload_intent_unavailable", StatusCodes.Status503ServiceUnavailable);
        }
    }

    public async Task<DocumentUploadResult<DocumentUploadConfirmationResult>> ConfirmAsync(
        ActorContext actor,
        ConfirmDocumentUploadCommand command,
        CancellationToken cancellationToken)
    {
        var payloadHash = ComputeConfirmationPayloadHash(command);
        try
        {
            var preparation = await PrepareConfirmationAsync(actor, command, payloadHash, cancellationToken);
            if (preparation.Result is not null)
            {
                return preparation.Result;
            }

            var lockKey = ComputeConfirmationLockKey(command);
            await database.Database.OpenConnectionAsync(cancellationToken);
            var lockAcquired = false;
            try
            {
                await AcquireConfirmationLockAsync(lockKey, cancellationToken);
                lockAcquired = true;

                preparation = await PrepareConfirmationAsync(actor, command, payloadHash, cancellationToken);
                if (preparation.Result is not null)
                {
                    return preparation.Result;
                }

                StorageObjectInspection inspection;
                string? inspectionFailure = null;
                try
                {
                    inspection = await storage.InspectObjectAsync(preparation.ProviderKey!, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogUnexpectedFailure(exception, InspectionOperationName, "storage_read_failed");
                    inspection = null!;
                    inspectionFailure = "storage_read_failed";
                }

                MalwareScanVerdict? verdict = null;
                if (inspectionFailure is null)
                {
                    verdict = await ScanForConfirmationAsync(preparation.ProviderKey!, cancellationToken);
                }

                return await FinalizeConfirmationAsync(
                    actor, command, payloadHash, inspection, inspectionFailure, verdict, cancellationToken);
            }
            finally
            {
                try
                {
                    if (lockAcquired)
                    {
                        await ReleaseConfirmationLockAsync(lockKey, CancellationToken.None);
                    }
                }
                finally
                {
                    await database.Database.CloseConnectionAsync();
                }
            }
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            return await ReturnConcurrentConfirmationReplayAsync(actor, command, payloadHash, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnexpectedFailure(exception, ConfirmationCommandName, "upload_confirmation_unavailable");
            return DocumentUploadResult<DocumentUploadConfirmationResult>.Failure("upload_confirmation_unavailable", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private async Task<ConfirmationPreparation> PrepareConfirmationAsync(
        ActorContext actor,
        ConfirmDocumentUploadCommand command,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
        var membership = await GetMembershipAsync(actor.UserId, command.CompanyId, cancellationToken);
        if (membership is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ConfirmationPreparation.Completed(await ReturnAuditedDenialAsync<DocumentUploadConfirmationResult>(
                actor, command.CompanyId, "membership_required", StatusCodes.Status403Forbidden, cancellationToken));
        }

        if (!await capabilityEvaluator.HasGrantAsync(command.CompanyId, membership.Id, ConfirmCapability, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ConfirmationPreparation.Completed(await ReturnAuditedDenialAsync<DocumentUploadConfirmationResult>(
                actor, command.CompanyId, "capability_denied", StatusCodes.Status403Forbidden, cancellationToken));
        }

        var existing = await FindIdempotencyAsync(actor.UserId, command.CompanyId, ConfirmationCommandName, command.RequestId, cancellationToken);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return !string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal)
                ? ConfirmationPreparation.Completed(await ReturnAuditedDenialAsync<DocumentUploadConfirmationResult>(
                    actor, command.CompanyId, "idempotency_conflict", StatusCodes.Status409Conflict, cancellationToken))
                : ConfirmationPreparation.Completed(DeserializeConfirmationResult(existing.ResultJson));
        }

        var preparedFile = await database.Files.SingleOrDefaultAsync(candidate =>
            candidate.CompanyId == command.CompanyId && candidate.Id == command.FileId,
            cancellationToken);
        var providerKey = preparedFile?.ProviderKey;
        var processingState = preparedFile?.ProcessingState;
        await transaction.RollbackAsync(cancellationToken);
        database.ChangeTracker.Clear();
        if (providerKey is null)
        {
            return ConfirmationPreparation.Completed(await ReturnAuditedDenialAsync<DocumentUploadConfirmationResult>(
                actor, command.CompanyId, "resource_not_found", StatusCodes.Status404NotFound, cancellationToken));
        }

        if (!string.Equals(processingState, FileRecord.QuarantinedState, StringComparison.Ordinal))
        {
            return ConfirmationPreparation.Completed(await ReturnAuditedDenialAsync<DocumentUploadConfirmationResult>(
                actor, command.CompanyId, "integrity_conflict", StatusCodes.Status409Conflict, cancellationToken));
        }

        return ConfirmationPreparation.ForInspection(providerKey);
    }

    /// <summary>
    /// Scan server-side com timeout próprio: falha ou timeout da engine nunca derruba a confirmação —
    /// produzem veredito indisponível (fail-closed) com motivo seguro. Cancelamento externo propaga.
    /// </summary>
    private async Task<MalwareScanVerdict> ScanForConfirmationAsync(
        string providerKey,
        CancellationToken cancellationToken)
    {
        using var scanScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scanScope.CancelAfter(malwareScannerOptions.Value.Timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // O conteúdo é aberto de forma lazy pelo scanner, direto do storage privado.
            return await malwareScanner.ScanAsync(
                new MalwareScanRequest(
                    providerKey,
                    scanToken => storage.OpenReadAsync(providerKey, scanToken)),
                scanScope.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return LogScanUnavailable(null, "scan_timeout", stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            return LogScanUnavailable(exception, "scan_failed", stopwatch.Elapsed);
        }
    }

    private MalwareScanVerdict LogScanUnavailable(Exception? exception, string reason, TimeSpan elapsed)
    {
        var elapsedMs = (long)elapsed.TotalMilliseconds;
        if (exception is null)
        {
            logger.LogWarning(
                ScanEventTemplate, ScanOperationName, ScanModuleName, "unavailable", reason, elapsedMs);
        }
        else
        {
            logger.LogWarning(
                exception, ScanEventTemplate, ScanOperationName, ScanModuleName, "unavailable", reason, elapsedMs);
        }

        return new MalwareScanVerdict(
            MalwareScanStatus.Unavailable, null, null, null, DateTimeOffset.UtcNow, null, reason);
    }

    private async Task<DocumentUploadResult<DocumentUploadConfirmationResult>> FinalizeConfirmationAsync(
        ActorContext actor,
        ConfirmDocumentUploadCommand command,
        string payloadHash,
        StorageObjectInspection inspection,
        string? inspectionFailure,
        MalwareScanVerdict? verdict,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
        var membership = await GetMembershipAsync(actor.UserId, command.CompanyId, cancellationToken);
        if (membership is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await ReturnAuditedDenialAsync<DocumentUploadConfirmationResult>(
                actor, command.CompanyId, "membership_required", StatusCodes.Status403Forbidden, cancellationToken);
        }

        if (!await capabilityEvaluator.HasGrantAsync(command.CompanyId, membership.Id, ConfirmCapability, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return await ReturnAuditedDenialAsync<DocumentUploadConfirmationResult>(
                actor, command.CompanyId, "capability_denied", StatusCodes.Status403Forbidden, cancellationToken);
        }

        var existing = await FindIdempotencyAsync(actor.UserId, command.CompanyId, ConfirmationCommandName, command.RequestId, cancellationToken);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return !string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal)
                ? await ReturnAuditedDenialAsync<DocumentUploadConfirmationResult>(
                    actor, command.CompanyId, "idempotency_conflict", StatusCodes.Status409Conflict, cancellationToken)
                : DeserializeConfirmationResult(existing.ResultJson);
        }

        var file = await database.Files.SingleOrDefaultAsync(candidate =>
            candidate.CompanyId == command.CompanyId && candidate.Id == command.FileId,
            cancellationToken);
        if (file is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await ReturnAuditedDenialAsync<DocumentUploadConfirmationResult>(
                actor, command.CompanyId, "resource_not_found", StatusCodes.Status404NotFound, cancellationToken);
        }

        if (!string.Equals(file.ProcessingState, FileRecord.QuarantinedState, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return await ReturnConcurrentConfirmationReplayAsync(actor, command, payloadHash, cancellationToken);
        }

        // Fail-closed: sem veredito do scanner a confirmação nunca libera.
        var verificationError = inspectionFailure
            ?? (verdict is null
                ? "malware_scan_unavailable"
                : DocumentVerificationPolicy.Validate(file, inspection, verdict));
        if (verificationError is not null)
        {
            if (!TerminalVerificationFailures.Contains(verificationError))
            {
                // Transitório (scanner/storage): nenhuma transição de estado — o documento
                // permanece em quarentena e a confirmação pode ser retentada sem reabri-lo.
                await transaction.RollbackAsync(cancellationToken);
                return await ReturnRetryableVerificationFailureAsync(
                    actor, command, file.Id, verificationError, cancellationToken);
            }

            return await CompleteVerificationFailureAsync(
                transaction, actor, command, payloadHash, file, verificationError, cancellationToken);
        }

        file.MarkVerified(actor.UserId, command.RequestId);
        file.MarkConfirmed(actor.UserId, command.RequestId);
        var result = new DocumentUploadConfirmationResult(file.Id, file.ProcessingState);
        database.IdempotencyRecords.Add(NewIdempotencyRecord(
            actor.UserId, command.CompanyId, ConfirmationCommandName, command.RequestId, payloadHash, result));
        await AppendAuditAsync(
            actor, command.CompanyId, ConfirmationCommandName, "file", file.Id, AuditOutcome.Success, null, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return DocumentUploadResult<DocumentUploadConfirmationResult>.Success(result);
    }

    private async Task<DocumentUploadResult<DocumentUploadConfirmationResult>> CompleteVerificationFailureAsync(
        IDbContextTransaction transaction,
        ActorContext actor,
        ConfirmDocumentUploadCommand command,
        string payloadHash,
        FileRecord file,
        string reason,
        CancellationToken cancellationToken)
    {
        file.MarkVerificationFailed(reason, actor.UserId, command.RequestId);
        var result = new DocumentUploadConfirmationResult(file.Id, file.ProcessingState);
        database.IdempotencyRecords.Add(NewIdempotencyRecord(
            actor.UserId, command.CompanyId, ConfirmationCommandName, command.RequestId, payloadHash, result));
        await AppendAuditAsync(
            actor, command.CompanyId, ConfirmationCommandName, "file", file.Id, AuditOutcome.Failure, reason, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return DocumentUploadResult<DocumentUploadConfirmationResult>.Success(result);
    }

    /// <summary>
    /// Falha transitória de verificação (scanner/storage): o registro do documento não é tocado —
    /// sem transição de estado, sem idempotência — e a confirmação pode ser retentada.
    /// </summary>
    private async Task<DocumentUploadResult<DocumentUploadConfirmationResult>> ReturnRetryableVerificationFailureAsync(
        ActorContext actor,
        ConfirmDocumentUploadCommand command,
        Guid fileId,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, command.CompanyId, cancellationToken);
            await AppendAuditAsync(
                actor, command.CompanyId, ConfirmationCommandName, "file", fileId, AuditOutcome.Failure, reason, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return DocumentUploadResult<DocumentUploadConfirmationResult>.Failure(
                reason, StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnexpectedFailure(exception, AuditOperationName, "audit_unavailable");
            return DocumentUploadResult<DocumentUploadConfirmationResult>.Failure(
                "upload_confirmation_unavailable", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private async Task<DocumentUploadResult<DocumentUploadIntentResult>> ReturnConcurrentIntentReplayAsync(
        ActorContext actor,
        CreateDocumentUploadIntentCommand command,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        var existing = await ReadIdempotencyAsync(actor, command.CompanyId, IntentCommandName, command.RequestId, cancellationToken);
        if (existing is null)
        {
            return await ReturnAuditedDenialAsync<DocumentUploadIntentResult>(
                actor, command.CompanyId, "integrity_conflict", StatusCodes.Status409Conflict, cancellationToken);
        }

        if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            return await ReturnAuditedDenialAsync<DocumentUploadIntentResult>(
                actor, command.CompanyId, "idempotency_conflict", StatusCodes.Status409Conflict, cancellationToken);
        }

        return await CreateIntentReplayAsync(actor, command.CompanyId, existing.ResultJson, cancellationToken);
    }

    private async Task<DocumentUploadResult<DocumentUploadConfirmationResult>> ReturnConcurrentConfirmationReplayAsync(
        ActorContext actor,
        ConfirmDocumentUploadCommand command,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        var existing = await ReadIdempotencyAsync(actor, command.CompanyId, ConfirmationCommandName, command.RequestId, cancellationToken);
        if (existing is null)
        {
            return await ReturnAuditedDenialAsync<DocumentUploadConfirmationResult>(
                actor, command.CompanyId, "integrity_conflict", StatusCodes.Status409Conflict, cancellationToken);
        }

        if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            return await ReturnAuditedDenialAsync<DocumentUploadConfirmationResult>(
                actor, command.CompanyId, "idempotency_conflict", StatusCodes.Status409Conflict, cancellationToken);
        }

        return DeserializeConfirmationResult(existing.ResultJson);
    }

    private async Task<IdempotencyRecord?> ReadIdempotencyAsync(
        ActorContext actor,
        Guid companyId,
        string commandName,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await ApplyContextAsync(actor, companyId, cancellationToken);
        var existing = await FindIdempotencyAsync(actor.UserId, companyId, commandName, requestId, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return existing;
    }

    private async Task<DocumentUploadResult<DocumentUploadIntentResult>> CreateIntentReplayAsync(
        ActorContext actor,
        Guid companyId,
        string resultJson,
        CancellationToken cancellationToken)
    {
        var storedResult = JsonSerializer.Deserialize<StoredIntentResult>(resultJson);
        if (storedResult is null)
        {
            return DocumentUploadResult<DocumentUploadIntentResult>.Failure(
                "upload_intent_unavailable", StatusCodes.Status503ServiceUnavailable);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await ApplyContextAsync(actor, companyId, cancellationToken);
        var file = await database.Files.SingleOrDefaultAsync(candidate =>
            candidate.CompanyId == companyId && candidate.Id == storedResult.FileId,
            cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        if (file is null)
        {
            return await ReturnAuditedDenialAsync<DocumentUploadIntentResult>(
                actor, companyId, "resource_not_found", StatusCodes.Status404NotFound, cancellationToken);
        }

        if (!string.Equals(file.ProcessingState, FileRecord.QuarantinedState, StringComparison.Ordinal))
        {
            return await ReturnAuditedDenialAsync<DocumentUploadIntentResult>(
                actor, companyId, "integrity_conflict", StatusCodes.Status409Conflict, cancellationToken);
        }

        return await CreateIntentResultAsync(
            new StoredIntentResult(file.Id, file.ProcessingState, file.ProviderKey),
            cancellationToken);
    }

    private async Task<DocumentUploadResult<DocumentUploadIntentResult>> CreateIntentResultAsync(
        StoredIntentResult storedResult,
        CancellationToken cancellationToken)
    {
        try
        {
            var uploadUrl = await storage.CreateSignedUploadUrlAsync(storedResult.ProviderKey, cancellationToken);
            return DocumentUploadResult<DocumentUploadIntentResult>.Success(
                new DocumentUploadIntentResult(storedResult.FileId, storedResult.ProcessingState, uploadUrl.Url));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnexpectedFailure(exception, IntentCommandName, "upload_intent_unavailable");
            return DocumentUploadResult<DocumentUploadIntentResult>.Failure("upload_intent_unavailable", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static DocumentUploadResult<DocumentUploadConfirmationResult> DeserializeConfirmationResult(string resultJson)
    {
        var result = JsonSerializer.Deserialize<DocumentUploadConfirmationResult>(resultJson);
        return result is null
            ? DocumentUploadResult<DocumentUploadConfirmationResult>.Failure("upload_confirmation_unavailable", StatusCodes.Status503ServiceUnavailable)
            : DocumentUploadResult<DocumentUploadConfirmationResult>.Success(result);
    }

    private async Task<DocumentUploadResult<T>> ReturnAuditedDenialAsync<T>(
        ActorContext actor,
        Guid companyId,
        string errorCode,
        int statusCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, companyId, cancellationToken);
            await AppendAuditAsync(actor, companyId, errorCode, "file", null, AuditOutcome.Denied, errorCode, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return DocumentUploadResult<T>.Failure(errorCode, statusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnexpectedFailure(exception, AuditOperationName, "audit_unavailable");
            return DocumentUploadResult<T>.Failure("audit_unavailable", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private Task AppendAuditAsync(
        ActorContext actor,
        Guid companyId,
        string action,
        string resourceType,
        Guid? resourceId,
        AuditOutcome outcome,
        string? reason,
        CancellationToken cancellationToken) =>
        auditAppender.AppendAsync(new AuditEventData(
            actor.UserId,
            AuditScope.Company,
            companyId,
            AuditSource.Web,
            action,
            resourceType,
            resourceId,
            outcome,
            new CorrelationContext(actor.CorrelationId).CorrelationId,
            reason,
            "{}"), cancellationToken);

    private void LogUnexpectedFailure(Exception exception, string operation, string errorCode) =>
        logger.LogError(
            exception,
            "Document upload operation {Operation} failed with code {ErrorCode}",
            operation,
            errorCode);

    private Task<Membership?> GetMembershipAsync(Guid actorUserId, Guid companyId, CancellationToken cancellationToken) =>
        database.Memberships.SingleOrDefaultAsync(
            membership => membership.CompanyId == companyId && membership.UserId == actorUserId,
            cancellationToken);

    private Task<IdempotencyRecord?> FindIdempotencyAsync(
        Guid actorUserId,
        Guid companyId,
        string commandName,
        Guid requestId,
        CancellationToken cancellationToken) =>
        database.IdempotencyRecords.SingleOrDefaultAsync(record =>
            record.CompanyId == companyId
            && record.ActorUserId == actorUserId
            && record.CommandName == commandName
            && record.RequestId == requestId,
            cancellationToken);

    private Task AcquireConfirmationLockAsync(
        long lockKey,
        CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_lock({lockKey})",
            cancellationToken);

    private Task ReleaseConfirmationLockAsync(
        long lockKey,
        CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_unlock({lockKey})",
            cancellationToken);

    private static long ComputeConfirmationLockKey(ConfirmDocumentUploadCommand command)
    {
        Span<byte> input = stackalloc byte[32];
        command.CompanyId.TryWriteBytes(input[..16]);
        command.FileId.TryWriteBytes(input[16..]);
        return BitConverter.ToInt64(SHA256.HashData(input));
    }

    private static IdempotencyRecord NewIdempotencyRecord<T>(
        Guid actorUserId,
        Guid companyId,
        string commandName,
        Guid requestId,
        string payloadHash,
        T result) =>
        new(Guid.NewGuid(), companyId, actorUserId, commandName, requestId, payloadHash, JsonSerializer.Serialize(result));

    private async Task ApplyContextAsync(ActorContext actor, Guid companyId, CancellationToken cancellationToken)
    {
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {actor.UserId.ToString()}, true)", cancellationToken);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {companyId.ToString()}, true)", cancellationToken);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.correlation_id', {new CorrelationContext(actor.CorrelationId).CorrelationId.ToString()}, true)", cancellationToken);
    }

    private static string ComputeIntentPayloadHash(CreateDocumentUploadIntentCommand command) =>
        ComputeHash(JsonSerializer.Serialize(new IntentIdempotencyPayload(
            command.CompanyId,
            command.FileName,
            command.MimeType,
            command.SizeBytes,
            command.Checksum,
            command.Classification,
            command.Origin,
            command.RetentionUntil.UtcDateTime.Ticks)));

    private static string ComputeConfirmationPayloadHash(ConfirmDocumentUploadCommand command) =>
        ComputeHash(JsonSerializer.Serialize(new ConfirmationIdempotencyPayload(command.CompanyId, command.FileId)));

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record StoredIntentResult(Guid FileId, string ProcessingState, string ProviderKey);
    private sealed record IntentIdempotencyPayload(
        Guid CompanyId,
        string FileName,
        string MimeType,
        long SizeBytes,
        string Checksum,
        string Classification,
        string Origin,
        long RetentionUtcTicks);
    private sealed record ConfirmationIdempotencyPayload(Guid CompanyId, Guid FileId);
    private sealed record ConfirmationPreparation(
        string? ProviderKey,
        DocumentUploadResult<DocumentUploadConfirmationResult>? Result)
    {
        public static ConfirmationPreparation ForInspection(string providerKey) => new(providerKey, null);

        public static ConfirmationPreparation Completed(DocumentUploadResult<DocumentUploadConfirmationResult> result) =>
            new(null, result);
    }
}
