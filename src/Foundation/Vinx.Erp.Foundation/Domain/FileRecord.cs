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
        if (retentionUntil <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionUntil));
        }

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
    public bool IsQuarantined => true;
    public Company Company { get; private set; } = null!;
    public Membership UploaderMembership { get; private set; } = null!;

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
