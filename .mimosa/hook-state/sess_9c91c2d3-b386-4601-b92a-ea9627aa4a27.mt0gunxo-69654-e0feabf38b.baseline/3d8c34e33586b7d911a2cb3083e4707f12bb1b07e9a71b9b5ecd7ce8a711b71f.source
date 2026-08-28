namespace Vinx.Erp.Foundation.Domain;

public sealed class UserSession : IConcurrencyVersioned
{
    private UserSession()
    {
    }

    public UserSession(
        Guid id,
        Guid userId,
        string issuer,
        Guid providerSessionId,
        DateTimeOffset providerAuthenticatedAt,
        DateTimeOffset tokenExpiresAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentOutOfRangeException.ThrowIfEqual(providerSessionId, Guid.Empty);

        if (providerAuthenticatedAt >= tokenExpiresAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerAuthenticatedAt),
                "Provider authentication must precede token expiration.");
        }

        Id = id;
        UserId = userId;
        Issuer = issuer;
        ProviderSessionId = providerSessionId;
        ProviderAuthenticatedAt = providerAuthenticatedAt;
        TokenExpiresAt = tokenExpiresAt;
        RowVersion = 1;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Issuer { get; private set; } = null!;
    public Guid ProviderSessionId { get; private set; }
    public DateTimeOffset ProviderAuthenticatedAt { get; private set; }
    public DateTimeOffset FirstSeenAt { get; private set; }
    public DateTimeOffset TokenExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? RevokedByUserId { get; private set; }
    public string? RevocationReason { get; private set; }
    public long RowVersion { get; private set; }

    public User User { get; private set; } = null!;
    public User? RevokedByUser { get; private set; }

    public void Revoke(Guid revokedByUserId, DateTimeOffset revokedAt, string reason)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(revokedByUserId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (RevokedAt is not null)
        {
            throw new InvalidOperationException("A revoked session cannot be revoked again.");
        }

        RevokedAt = revokedAt;
        RevokedByUserId = revokedByUserId;
        RevocationReason = reason;
    }
}
