using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Notifications;

/// <summary>Entrada confiável já baixada pelo adapter de canal.</summary>
public sealed record InboundAttachmentMaterializationRequest(
    Guid InboundMessageId,
    ActorContext Actor,
    Guid CompanyId,
    Guid MembershipId,
    string MimeType,
    ReadOnlyMemory<byte> Bytes);

/// <summary>Resultado fechado; somente um FileRecord confirmado pode avançar.</summary>
public sealed record InboundAttachmentMaterializationResult(
    Guid? ConfirmedFileId,
    string? FailureReason)
{
    public bool IsConfirmed => ConfirmedFileId is not null && FailureReason is null;

    public static InboundAttachmentMaterializationResult Confirmed(Guid fileId) =>
        new(fileId, null);

    public static InboundAttachmentMaterializationResult Failed(string reason) =>
        new(null, reason);
}

/// <summary>
/// Materializa bytes admitidos no ciclo de vida existente de FileRecord. A
/// implementação é intencionalmente estreita para os adapters WhatsApp e Telegram.
/// </summary>
public sealed class InboundAttachmentMaterializer(
    FoundationDbContext database,
    FileStorage storage,
    IMalwareScanner malwareScanner,
    IOptions<MalwareScannerOptions> scannerOptions,
    CapabilityEvaluator capabilityEvaluator)
{
    private static readonly CapabilityRequirement UploadCapability =
        new(KnownCapabilities.FilesUpload, CapabilityScope.Company);
    private static readonly CapabilityRequirement ReadOcrCapability =
        new(KnownCapabilities.FilesReadOcr, CapabilityScope.Company);
    private static readonly IReadOnlyDictionary<string, string> Extensions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["application/pdf"] = ".pdf",
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
        };

    public async Task<InboundAttachmentMaterializationResult> MaterializeAsync(
        InboundAttachmentMaterializationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InboundMessageId == Guid.Empty
            || request.CompanyId == Guid.Empty
            || request.MembershipId == Guid.Empty
            || request.Actor.UserId == Guid.Empty
            || !Extensions.TryGetValue(request.MimeType, out var extension))
        {
            return InboundAttachmentMaterializationResult.Failed("document_rejected");
        }

        var checksum = ChecksumOf(request.Bytes.Span);
        var fileName = $"agent-{request.InboundMessageId:N}{extension}";
        if (DocumentUploadPolicy.Validate(fileName, request.MimeType, request.Bytes.Length, checksum) is not null)
        {
            return InboundAttachmentMaterializationResult.Failed("document_rejected");
        }

        try
        {
            if (request.MimeType == "application/pdf")
            {
                using var pdf = new MemoryStream(request.Bytes.ToArray(), writable: false);
                _ = DocumentTextExtractor.Extract(
                    pdf,
                    new DocumentContextLimits(),
                    cancellationToken);
            }
            else
            {
                _ = ImageCanonicalizer.Canonicalize(
                    request.Bytes.ToArray(),
                    new DocumentContextLimits());
            }
        }
        catch (DocumentContextValidationException)
        {
            return InboundAttachmentMaterializationResult.Failed("document_rejected");
        }

        if (!await IsAuthorizedAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return InboundAttachmentMaterializationResult.Failed("capability_missing");
        }

        var fileId = request.InboundMessageId;
        var existing = await FindExistingAsync(request, fileId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return IsMatchingConfirmedFile(existing, request, fileName, extension, checksum)
                ? InboundAttachmentMaterializationResult.Confirmed(fileId)
                : InboundAttachmentMaterializationResult.Failed("document_rejected");
        }

        MalwareScanVerdict verdict;
        using (var scanTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            scanTimeout.CancelAfter(scannerOptions.Value.Timeout);
            try
            {
                verdict = await malwareScanner.ScanAsync(
                    new MalwareScanRequest(
                        $"agent/{request.InboundMessageId:N}",
                        _ => Task.FromResult<Stream>(new MemoryStream(request.Bytes.ToArray(), writable: false))),
                    scanTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return InboundAttachmentMaterializationResult.Failed("document_scan_unavailable");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return InboundAttachmentMaterializationResult.Failed("document_scan_unavailable");
            }
        }

        if (verdict.Status != MalwareScanStatus.Clean
            || string.IsNullOrWhiteSpace(verdict.Engine)
            || string.IsNullOrWhiteSpace(verdict.EngineVersion)
            || string.IsNullOrWhiteSpace(verdict.SignatureVersion)
            || verdict.ScannedAtUtc == default
            || !string.Equals(verdict.ContentChecksum, checksum, StringComparison.OrdinalIgnoreCase))
        {
            return InboundAttachmentMaterializationResult.Failed(
                verdict.Status == MalwareScanStatus.MalwareDetected
                    ? "document_rejected"
                    : "document_scan_unavailable");
        }

        var file = new FileRecord(
            fileId,
            request.CompanyId,
            request.MembershipId,
            fileName,
            extension,
            request.Bytes.Length,
            request.MimeType,
            checksum,
            "agentic_attachment",
            "agent_channel",
            DateTimeOffset.UtcNow.AddDays(30),
            request.Actor.UserId,
            request.InboundMessageId);

        try
        {
            await storage.EnsurePrivateContainerAsync(cancellationToken).ConfigureAwait(false);
            await storage.UploadObjectAsync(
                file.ProviderKey,
                request.Bytes,
                request.MimeType,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return InboundAttachmentMaterializationResult.Failed("document_storage_unavailable");
        }
        catch (HttpRequestException)
        {
            throw new NotificationProviderTransientException(
                "Inbound attachment storage failed transiently.");
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await FoundationRlsContext.ApplyCommandAsync(
                database.Database,
                request.Actor,
                request.CompanyId,
                cancellationToken).ConfigureAwait(false);
            if (!await IsAuthorizedInCurrentTransactionAsync(request, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                database.ChangeTracker.Clear();
                await DeleteIfUnownedAfterRollbackAsync(
                    request,
                    file).ConfigureAwait(false);
                return InboundAttachmentMaterializationResult.Failed("capability_missing");
            }

            file.MarkVerified(request.Actor.UserId, request.InboundMessageId);
            file.MarkConfirmed(request.Actor.UserId, request.InboundMessageId);
            file.RecordScan(
                verdict.Engine,
                verdict.EngineVersion,
                verdict.SignatureVersion,
                verdict.ScannedAtUtc);
            database.Files.Add(file);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return InboundAttachmentMaterializationResult.Confirmed(fileId);
        }
        catch
        {
            var rollbackKnown = await TryRollbackAsync(transaction).ConfigureAwait(false);
            // Libera a transação envenenada (ack pós-commit deixa CurrentTransaction
            // ativo) antes da releitura de recuperação abrir a própria transação.
            await transaction.DisposeAsync().ConfigureAwait(false);
            database.ChangeTracker.Clear();
            var recovered = await TryReadExistingAsync(
                request,
                fileId,
                CancellationToken.None).ConfigureAwait(false);
            if (recovered.ReadSucceeded && recovered.File is not null)
            {
                if (IsMatchingConfirmedFile(recovered.File, request, fileName, extension, checksum))
                {
                    return InboundAttachmentMaterializationResult.Confirmed(fileId);
                }

                throw;
            }

            if (rollbackKnown && recovered.ReadSucceeded)
            {
                await DeleteUploadedObjectAsync(file.ProviderKey).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<bool> IsAuthorizedAsync(
        InboundAttachmentMaterializationRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await FoundationRlsContext.ApplyCommandAsync(
            database.Database,
            request.Actor,
            request.CompanyId,
            cancellationToken).ConfigureAwait(false);
        var allowed = await IsAuthorizedInCurrentTransactionAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        database.ChangeTracker.Clear();
        return allowed;
    }

    private async Task<FileRecord?> FindExistingAsync(
        InboundAttachmentMaterializationRequest request,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await FoundationRlsContext.ApplyCommandAsync(
            database.Database,
            request.Actor,
            request.CompanyId,
            cancellationToken).ConfigureAwait(false);
        var existing = await database.Files
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(file => file.Id == fileId, cancellationToken)
            .ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        database.ChangeTracker.Clear();
        return existing;
    }

    private async Task<(bool ReadSucceeded, FileRecord? File)> TryReadExistingAsync(
        InboundAttachmentMaterializationRequest request,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            return (true, await FindExistingAsync(request, fileId, cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            return (false, null);
        }
    }

    private async Task DeleteIfUnownedAfterRollbackAsync(
        InboundAttachmentMaterializationRequest request,
        FileRecord file)
    {
        var recovered = await TryReadExistingAsync(
            request,
            file.Id,
            CancellationToken.None).ConfigureAwait(false);
        if (!recovered.ReadSucceeded || recovered.File is not null)
        {
            return;
        }

        await DeleteUploadedObjectAsync(file.ProviderKey).ConfigureAwait(false);
    }

    private static bool IsMatchingConfirmedFile(
        FileRecord file,
        InboundAttachmentMaterializationRequest request,
        string fileName,
        string extension,
        string checksum) =>
        file.Id == request.InboundMessageId
        && file.CompanyId == request.CompanyId
        && file.UploaderMembershipId == request.MembershipId
        && file.OriginalFileName == fileName
        && file.Extension == extension
        && file.SizeBytes == request.Bytes.Length
        && file.MimeType == request.MimeType
        && string.Equals(file.Checksum, checksum, StringComparison.OrdinalIgnoreCase)
        && file.Classification == "agentic_attachment"
        && file.Origin == "agent_channel"
        && file.ProviderKey == $"quarantine/{request.CompanyId:N}/{request.InboundMessageId:N}"
        && file.ProcessingState == FileRecord.ConfirmedState
        && file.DeletedAt is null
        && file.RetentionUntil > DateTimeOffset.UtcNow
        && !string.IsNullOrWhiteSpace(file.ScanEngine)
        && !string.IsNullOrWhiteSpace(file.ScanEngineVersion)
        && !string.IsNullOrWhiteSpace(file.ScanSignatureVersion)
        && file.ScannedAtUtc is not null;

    private static async Task<bool> TryRollbackAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsAuthorizedInCurrentTransactionAsync(
        InboundAttachmentMaterializationRequest request,
        CancellationToken cancellationToken)
    {
        var membershipExists = await database.Memberships.AnyAsync(
            membership => membership.Id == request.MembershipId
                && membership.CompanyId == request.CompanyId
                && membership.UserId == request.Actor.UserId
                && membership.DeletedAt == null,
            cancellationToken).ConfigureAwait(false);
        return membershipExists
            && await capabilityEvaluator.HasGrantAsync(
                request.CompanyId,
                request.MembershipId,
                UploadCapability,
                cancellationToken).ConfigureAwait(false)
            && await capabilityEvaluator.HasGrantAsync(
                request.CompanyId,
                request.MembershipId,
                ReadOcrCapability,
                cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteUploadedObjectAsync(string providerKey)
    {
        try
        {
            await storage.DeleteObjectAsync(providerKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // O objeto permanece privado e sem FileRecord; limpeza operacional pode reconciliá-lo.
        }
    }

    private static string ChecksumOf(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
}
