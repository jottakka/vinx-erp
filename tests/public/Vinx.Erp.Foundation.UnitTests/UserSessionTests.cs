using Vinx.Erp.Foundation.Domain;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class UserSessionTests
{
    [Fact]
    public void Create_rejects_authentication_at_or_after_token_expiration()
    {
        var authenticatedAt = DateTimeOffset.Parse("2026-08-09T12:00:00+00:00");

        Assert.Throws<ArgumentOutOfRangeException>(() => new UserSession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://project.supabase.co/auth/v1",
            Guid.NewGuid(),
            authenticatedAt,
            authenticatedAt));
    }

    [Fact]
    public void Revoke_rejects_empty_reason_without_changing_the_session()
    {
        var session = CreateActiveSession();

        Assert.Throws<ArgumentException>(() => session.Revoke(
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-08-09T12:01:00+00:00"),
            " "));

        Assert.Null(session.RevokedAt);
        Assert.Null(session.RevokedByUserId);
        Assert.Null(session.RevocationReason);
    }

    [Fact]
    public void Create_allows_an_active_session_with_a_future_expiration()
    {
        var session = CreateActiveSession();

        Assert.Null(session.RevokedAt);
        Assert.Null(session.RevokedByUserId);
        Assert.Null(session.RevocationReason);
        Assert.True(session.ProviderAuthenticatedAt < session.TokenExpiresAt);
    }

    private static UserSession CreateActiveSession() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "https://project.supabase.co/auth/v1",
        Guid.NewGuid(),
        DateTimeOffset.Parse("2026-08-09T12:00:00+00:00"),
        DateTimeOffset.Parse("2026-08-09T13:00:00+00:00"));
}
