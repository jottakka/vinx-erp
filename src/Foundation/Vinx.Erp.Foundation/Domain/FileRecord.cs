namespace Vinx.Erp.Foundation.Domain;

public sealed class FileRecord : SoftDeletableEntity, ICompanyScoped
{
    public const string QuarantinedState = "quarantined";
    public const string VerifiedState = "verified";
    public const string ConfirmedState = "confirmed";
    public const string VerificationFailedState = "verification_failed";

    private FileRecord()
    {
    }

    public FileRecord(
        Guid id,
        Guid companyId,
        Guid uploaderMembershipId,
        string originalFileName,
        string extension,
        long sizeBytes,
        string mimeType,
        string checksum,
        string classification,
        string origin,
        DateTimeOffset retentionUntil,
        Guid actorUserId,
        Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(checksum);
        ArgumentException.ThrowIfNullOrWhiteSpace(classification);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retentionUntil, DateTimeOffset.UtcNow);

        CompanyId = companyId;
        UploaderMembershipId = uploaderMembershipId;
        OriginalFileName = originalFileName;
        Extension = extension;
        SizeBytes = sizeBytes;
        MimeType = mimeType;
        Checksum = checksum;
        Classification = classification;
        Origin = origin;
        RetentionUntil = retentionUntil;
        ProviderKey = $"quarantine/{companyId:N}/{id:N}";
        ProcessingState = QuarantinedState;
        InitializeLifecycle(id, actorUserId, requestId);
    }

    public Guid CompanyId { get; private set; }
    public Guid UploaderMembershipId { get; private set; }
    public string OriginalFileName { get; private set; } = null!;
    public string Extension { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public string MimeType { get; private set; } = null!;
    public string Checksum { get; private set; } = null!;
    public string Classification { get; private set; } = null!;
    public string Origin { get; private set; } = null!;
    public DateTimeOffset RetentionUntil { get; private set; }
    public string ProviderKey { get; private set; } = null!;
    public string ProcessingState { get; private set; } = null!;
    public string? FailureReason { get; private set; }

    /// <summary>Engine de antimalware que produziu o veredito durável (ex.: "ClamAV"); bind de <c>MalwareScanVerdict.Engine</c>.</summary>
    public string? ScanEngine { get; private set; }

    /// <summary>Versão da engine do scan durável; bind de <c>MalwareScanVerdict.EngineVersion</c>.</summary>
    public string? ScanEngineVersion { get; private set; }

    /// <summary>Versão de assinatura do scan durável; bind de <c>MalwareScanVerdict.SignatureVersion</c>.</summary>
    public string? ScanSignatureVersion { get; private set; }

    /// <summary>Momento UTC do scan durável; bind de <c>MalwareScanVerdict.ScannedAtUtc</c>.</summary>
    public DateTimeOffset? ScannedAtUtc { get; private set; }

#pragma warning disable CA1822 // A propriedade faz parte do contrato de entidade e é lida por instâncias nos testes e mapeamentos.
    public bool IsQuarantined => true;
#pragma warning restore CA1822
    public Company Company { get; private set; } = null!;
    public Membership UploaderMembership { get; private set; } = null!;

    /// <summary>
    /// Registra os metadados do veredito de antimalware. Chamado apenas em saídas duráveis
    /// (confirmação clean e falha terminal <c>malware_detected</c>); não altera <see cref="ProcessingState"/>.
    /// </summary>
    public void RecordScan(string engine, string engineVersion, string signatureVersion, DateTimeOffset scannedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(signatureVersion);
        if (scannedAtUtc == default)
        {
            throw new ArgumentException("Scan timestamp is required.", nameof(scannedAtUtc));
        }

        ScanEngine = engine;
        ScanEngineVersion = engineVersion;
        ScanSignatureVersion = signatureVersion;
        ScannedAtUtc = scannedAtUtc;
    }

    public void MarkVerified(Guid actorUserId, Guid requestId)
    {
        EnsureState(QuarantinedState);
        ProcessingState = VerifiedState;
        RecordUpdate(actorUserId, requestId);
    }

    public void MarkConfirmed(Guid actorUserId, Guid requestId)
    {
        EnsureState(VerifiedState);
        ProcessingState = ConfirmedState;
        RecordUpdate(actorUserId, requestId);
    }

    public void MarkVerificationFailed(string reason, Guid actorUserId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureState(QuarantinedState);
        FailureReason = reason;
        ProcessingState = VerificationFailedState;
        RecordUpdate(actorUserId, requestId);
    }

    public void MarkDeleted(string reason, Guid actorUserId, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (DeletedAt is not null)
        {
            return;
        }

        DeletedAt = DateTimeOffset.UtcNow;
        DeletedByUserId = actorUserId;
        DeletedReason = reason;
        RecordUpdate(actorUserId, requestId);
    }

    public void Restore(Guid actorUserId, Guid requestId)
    {
        if (DeletedAt is null)
        {
            throw new InvalidOperationException("Document is not deleted.");
        }

        DeletedAt = null;
        DeletedByUserId = null;
        DeletedReason = null;
        RecordUpdate(actorUserId, requestId);
    }

    public bool IsPhysicalDeletionAllowed(DateTimeOffset now) =>
        DeletedAt is not null && RetentionUntil <= now;

    private void EnsureState(string expectedState)
    {
        if (!string.Equals(ProcessingState, expectedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Document transition requires state {expectedState}, current state is {ProcessingState}.");
        }
    }
}
