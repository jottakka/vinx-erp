using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.AI.DocumentContext;

/// <summary>
/// Fronteira de leitura autorizada sobre FileRecord/Foundation: RLS
/// transacional, membership ativa, capability dedicada
/// <c>foundation.files.read-ocr</c> (escopo Company, menor privilégio),
/// liberação = <c>confirmed</c> COM veredito de antimalware durável,
/// retenção viva, tamanho dentro da política de upload e integridade
/// checksum/size do storage ANTES de abrir. O stream devolvido é seekable e
/// limitado exatamente a <c>SizeBytes</c>. Toda decisão (allow e deny) é
/// auditada na mesma fronteira sem conteúdo; a leitura re-autoriza a cada
/// chamada (sem cache — revogação é imediata).
/// </summary>
public sealed class FoundationAuthorizedDocumentSource(
    FoundationDbContext database,
    FileStorage storage,
    CapabilityEvaluator capabilityEvaluator,
    AuditAppender auditAppender,
    ILogger<FoundationAuthorizedDocumentSource> logger) : IAuthorizedDocumentSource
{
    public const string CommandName = "foundation.files.read-ocr";

    private const string ResourceType = "file";
    private const string MembershipRequired = "membership_required";
    private const string CapabilityDenied = "capability_denied";
    private const string ResourceNotFound = "resource_not_found";
    private const string DocumentNotReleased = "document_not_released";
    private const string RetentionExpired = "retention_expired";
    private const string DocumentSizeOutOfPolicy = "document_size_out_of_policy";
    private const string DocumentChanged = "document_changed";
    private const string ChecksumMismatch = "checksum_mismatch";
    private const string ObjectSizeDivergence = "object_size_divergence";
    private const string StorageUnavailable = "storage_unavailable";
    private const string AuditUnavailable = "audit_unavailable";
    private const string FailureTemplate = "Authorized document read failed with code {ErrorCode}";

    private static readonly CapabilityRequirement ReadOcrCapability =
        new(KnownCapabilities.FilesReadOcr, CapabilityScope.Company);

    public async Task<AuthorizedDocument> OpenReadAsync(
        ActorContext actor,
        Guid companyId,
        Guid fileRecordId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var file = await ResolvePreflightAsync(actor, companyId, fileRecordId, cancellationToken);
        var fileTuple = AuthorizedFileTuple.From(file);

        // Integridade antes de abrir: checksum E tamanho do objeto devem
        // bater com o registro (chave imutável; sem re-hash do conteúdo).
        StorageObjectMetadata metadata;
        try
        {
            metadata = await storage.GetObjectMetadataAsync(file.ProviderKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await DenyWithLogAsync(actor, companyId, fileRecordId, StorageUnavailable, exception, cancellationToken);
        }

        if (!string.Equals(metadata.Checksum, file.Checksum, StringComparison.OrdinalIgnoreCase)
            || metadata.SizeBytes != file.SizeBytes)
        {
            return await DenyAsync(actor, companyId, fileRecordId, ChecksumMismatch, cancellationToken);
        }

        // Stream seekable limitado: cópia de EXATAMENTE SizeBytes bytes —
        // qualquer byte a mais ou a menos no objeto é divergência.
        byte[]? buffer;
        try
        {
            buffer = await ReadExactlyAsync(file, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await DenyWithLogAsync(actor, companyId, fileRecordId, StorageUnavailable, exception, cancellationToken);
        }

        if (buffer is null)
        {
            return await DenyAsync(actor, companyId, fileRecordId, ObjectSizeDivergence, cancellationToken);
        }

        // O storage pode ter demorado: descarte entidades do preflight e
        // reautorize imediatamente antes de liberar os bytes. O allow é
        // auditado atomicamente com esse snapshot final.
        database.ChangeTracker.Clear();
        file = await ReauthorizeAndAuditAllowAsync(
            actor,
            companyId,
            fileRecordId,
            fileTuple,
            cancellationToken);

        return new AuthorizedDocument(
            fileRecordId,
            companyId,
            file.MimeType,
            file.Classification,
            new MemoryStream(buffer, 0, buffer.Length, writable: false));
    }

    private async Task<FileRecord> ResolvePreflightAsync(
        ActorContext actor,
        Guid companyId,
        Guid fileRecordId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        await FoundationRlsContext.ApplyCommandAsync(database.Database, actor, companyId, cancellationToken);
        var (file, denial) = await ResolveAuthorizedFileAsync(actor, companyId, fileRecordId, cancellationToken);
        if (denial is not null)
        {
            await CommitAuditOrThrowAsync(
                transaction,
                actor,
                companyId,
                fileRecordId,
                AuditOutcome.Denied,
                denial,
                cancellationToken);

            throw new DocumentReadDeniedException(denial);
        }

        await transaction.RollbackAsync(cancellationToken);
        database.ChangeTracker.Clear();
        return file;
    }

    private async Task<FileRecord> ReauthorizeAndAuditAllowAsync(
        ActorContext actor,
        Guid companyId,
        Guid fileRecordId,
        AuthorizedFileTuple fileTuple,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        await FoundationRlsContext.ApplyCommandAsync(database.Database, actor, companyId, cancellationToken);
        var (file, denial) = await ResolveAuthorizedFileAsync(actor, companyId, fileRecordId, cancellationToken);
        denial ??= fileTuple.Matches(file) ? null : DocumentChanged;
        if (denial is not null)
        {
            await CommitAuditOrThrowAsync(
                transaction,
                actor,
                companyId,
                fileRecordId,
                AuditOutcome.Denied,
                denial,
                cancellationToken);

            throw new DocumentReadDeniedException(denial);
        }

        await CommitAuditOrThrowAsync(
            transaction,
            actor,
            companyId,
            fileRecordId,
            AuditOutcome.Success,
            null,
            cancellationToken);

        return file;
    }

    private async Task<(FileRecord File, string? Denial)> ResolveAuthorizedFileAsync(
        ActorContext actor,
        Guid companyId,
        Guid fileRecordId,
        CancellationToken cancellationToken)
    {
        // Membership revogada (soft delete, sem query filter global no mapa
        // de memberships) não autoriza leitura.
        var membership = await database.Memberships.SingleOrDefaultAsync(
            membership => membership.CompanyId == companyId
                && membership.UserId == actor.UserId
                && membership.DeletedAt == null,
            cancellationToken);
        if (membership is null)
        {
            return (null!, MembershipRequired);
        }

        if (!await capabilityEvaluator.HasGrantAsync(companyId, membership.Id, ReadOcrCapability, cancellationToken))
        {
            return (null!, CapabilityDenied);
        }

        var file = await database.Files.SingleOrDefaultAsync(
            candidate => candidate.CompanyId == companyId
                && candidate.Id == fileRecordId
                && candidate.DeletedAt == null,
            cancellationToken);
        if (file is null)
        {
            // Documento de outra empresa é invisível sob RLS: mesma negação.
            return (null!, ResourceNotFound);
        }

        // Liberação exige confirmed COM veredito durável completo: a
        // transição de estado sozinha não prova scan.
        if (!string.Equals(file.ProcessingState, FileRecord.ConfirmedState, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(file.ScanEngine)
            || string.IsNullOrWhiteSpace(file.ScanEngineVersion)
            || string.IsNullOrWhiteSpace(file.ScanSignatureVersion)
            || file.ScannedAtUtc is null)
        {
            return (file, DocumentNotReleased);
        }

        if (file.RetentionUntil <= DateTimeOffset.UtcNow)
        {
            return (file, RetentionExpired);
        }

        // Guarda antes de qualquer alocação: o registro tem que caber na
        // política de upload (1..25 MiB) vigente na fronteira.
        if (file.SizeBytes is < 1 or > DocumentUploadPolicy.MaxSizeBytes)
        {
            return (file, DocumentSizeOutOfPolicy);
        }

        return (file, null);
    }

    private async Task<byte[]?> ReadExactlyAsync(FileRecord file, CancellationToken cancellationToken)
    {
        var stream = await storage.OpenReadAsync(file.ProviderKey, cancellationToken);
        await using (stream)
        {
            var buffer = new byte[file.SizeBytes];
            var total = 0;
            int read;
            while (total < buffer.Length
                && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
            {
                total += read;
            }

            if (total != buffer.Length)
            {
                return null;
            }

            // O byte de guarda: um objeto maior que o registrado diverge.
            var guard = new byte[1];
            if (await stream.ReadAsync(guard.AsMemory(0, 1), cancellationToken) is not 0)
            {
                return null;
            }

            return buffer;
        }
    }

    private async Task<AuthorizedDocument> DenyAsync(
        ActorContext actor,
        Guid companyId,
        Guid fileRecordId,
        string reason,
        CancellationToken cancellationToken)
    {
        await AuditAsync(actor, companyId, fileRecordId, AuditOutcome.Denied, reason, cancellationToken);

        throw new DocumentReadDeniedException(reason);
    }

    private async Task<AuthorizedDocument> DenyWithLogAsync(
        ActorContext actor,
        Guid companyId,
        Guid fileRecordId,
        string reason,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(exception, FailureTemplate, reason);
        return await DenyAsync(actor, companyId, fileRecordId, reason, cancellationToken);
    }

    private async Task AuditAsync(
        ActorContext actor,
        Guid companyId,
        Guid fileRecordId,
        AuditOutcome outcome,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await FoundationRlsContext.ApplyCommandAsync(database.Database, actor, companyId, cancellationToken);
            await CommitAuditAsync(
                transaction,
                actor,
                companyId,
                fileRecordId,
                outcome,
                reason,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw AuditFailure(exception);
        }
    }

    private async Task CommitAuditOrThrowAsync(
        IDbContextTransaction transaction,
        ActorContext actor,
        Guid companyId,
        Guid fileRecordId,
        AuditOutcome outcome,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await CommitAuditAsync(
                transaction,
                actor,
                companyId,
                fileRecordId,
                outcome,
                reason,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw AuditFailure(exception);
        }
    }

    private async Task CommitAuditAsync(
        IDbContextTransaction transaction,
        ActorContext actor,
        Guid companyId,
        Guid fileRecordId,
        AuditOutcome outcome,
        string? reason,
        CancellationToken cancellationToken)
    {
        await auditAppender.AppendAsync(
            new AuditEventData(
                actor.UserId,
                AuditScope.Company,
                companyId,
                AuditSource.Ai,
                CommandName,
                ResourceType,
                fileRecordId,
                outcome,
                new CorrelationContext(actor.CorrelationId).CorrelationId,
                reason,
                "{}"),
            cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private DocumentReadDeniedException AuditFailure(Exception exception)
    {
        logger.LogError(exception, FailureTemplate, AuditUnavailable);
        return new DocumentReadDeniedException(AuditUnavailable);
    }

    private sealed record AuthorizedFileTuple(
        string ProviderKey,
        long SizeBytes,
        string Checksum,
        string MimeType,
        string Classification)
    {
        public static AuthorizedFileTuple From(FileRecord file) =>
            new(file.ProviderKey, file.SizeBytes, file.Checksum, file.MimeType, file.Classification);

        public bool Matches(FileRecord file) =>
            string.Equals(ProviderKey, file.ProviderKey, StringComparison.Ordinal)
            && SizeBytes == file.SizeBytes
            && string.Equals(Checksum, file.Checksum, StringComparison.OrdinalIgnoreCase)
            && string.Equals(MimeType, file.MimeType, StringComparison.Ordinal)
            && string.Equals(Classification, file.Classification, StringComparison.Ordinal);
    }
}
