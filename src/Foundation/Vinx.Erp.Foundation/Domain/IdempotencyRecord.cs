namespace Vinx.Erp.Foundation.Domain;

public sealed class IdempotencyRecord : ICompanyScoped
{
    private IdempotencyRecord()
    {
    }

    public IdempotencyRecord(
        Guid id,
        Guid companyId,
        Guid actorUserId,
        string commandName,
        Guid requestId,
        string payloadHash,
        string resultJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);
        Id = id;
        CompanyId = companyId;
        ActorUserId = actorUserId;
        CommandName = commandName;
        RequestId = requestId;
        PayloadHash = payloadHash;
        ResultJson = resultJson;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public string CommandName { get; private set; } = null!;
    public Guid RequestId { get; private set; }
    public string PayloadHash { get; private set; } = null!;
    public string ResultJson { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public Company Company { get; private set; } = null!;
}
