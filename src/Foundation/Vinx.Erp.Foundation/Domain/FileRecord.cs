namespace Vinx.Erp.Foundation.Domain;

public sealed class FileRecord : SoftDeletableEntity, ICompanyScoped
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
        CompanyId = companyId;
        UploaderMembershipId = uploaderMembershipId;
        Classification = classification;
        Checksum = checksum;
        ProviderKey = providerKey;
        IsQuarantined = isQuarantined;
        InitializeLifecycle(id, actorUserId, requestId);
    }

    public Guid CompanyId { get; private set; }
    public Guid UploaderMembershipId { get; private set; }
    public string Classification { get; private set; } = null!;
    public string Checksum { get; private set; } = null!;
    public string ProviderKey { get; private set; } = null!;
    public bool IsQuarantined { get; private set; }
    public Company Company { get; private set; } = null!;
    public Membership UploaderMembership { get; private set; } = null!;
}
