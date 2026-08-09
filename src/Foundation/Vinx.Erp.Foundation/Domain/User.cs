namespace Vinx.Erp.Foundation.Domain;

public sealed class User
{
    private User()
    {
    }

    public User(Guid id, string displayName, string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        Id = id;
        DisplayName = displayName;
        Email = email;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public string DisplayName { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
}
