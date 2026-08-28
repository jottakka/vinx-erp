namespace Vinx.Erp.Foundation.Domain;

public sealed class ExternalIdentity
{
    private ExternalIdentity()
    {
    }

    public ExternalIdentity(Guid id, Guid userId, string issuer, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        Id = id;
        UserId = userId;
        Issuer = issuer;
        Subject = subject;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Issuer { get; private set; } = null!;
    public string Subject { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public User User { get; private set; } = null!;
}
