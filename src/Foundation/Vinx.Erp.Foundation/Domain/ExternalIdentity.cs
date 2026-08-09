namespace Vinx.Erp.Foundation.Domain;

public sealed class ExternalIdentity
{
    private ExternalIdentity()
    {
    }

    public ExternalIdentity(Guid id, Guid userId, string provider, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        Id = id;
        UserId = userId;
        Provider = provider;
        Subject = subject;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Provider { get; private set; } = null!;
    public string Subject { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public User User { get; private set; } = null!;
}
