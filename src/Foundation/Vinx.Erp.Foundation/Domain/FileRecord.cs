namespace Vinx.Erp.Foundation.Domain;

public sealed class FileRecord
{
    private FileRecord()
    {
    }

    public FileRecord(
        Guid id,
        Guid companyId,
        Guid uploaderMembershipId,
        string classification,
        string checksum,
        string providerKey,
        bool isQuarantined,
        Guid actorUserId,
        Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(classification);
        ArgumentException.ThrowIfNullOrWhiteSpace(checksum);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        Id = id;
        CompanyId = companyId;
        UploaderMembershipId = uploaderMembershipId;
        Classification = classification;
        Checksum = checksum;
        ProviderKey = providerKey;
        IsQuarantined = isQuarantined;
        CreatedByUserId = actorUserId;
        UpdatedByUserId = actorUserId;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        RequestId = requestId;
        RowVersion = 1;
    }

    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid UploaderMembershipId { get; private set; }
    public string Classification { get; private set; } = null!;
    public string Checksum { get; private set; } = null!;
    public string ProviderKey { get; private set; } = null!;
    public bool IsQuarantined { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public Guid UpdatedByUserId { get; private set; }
    public Guid RequestId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedByUserId { get; private set; }
    public string? DeletedReason { get; private set; }
    public long RowVersion { get; private set; }

    public Company Company { get; private set; } = null!;
    public Membership UploaderMembership { get; private set; } = null!;
}
