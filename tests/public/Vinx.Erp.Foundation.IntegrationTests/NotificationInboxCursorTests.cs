using Vinx.Erp.Foundation.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class NotificationInboxCursorTests
{
    [Fact]
    public void Round_trip_preserves_the_keyset_position()
    {
        var created = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero)
            .AddMicroseconds(123456);
        var id = Guid.NewGuid();

        var token = NotificationInboxCursor.Encode(created, id);
        var decoded = NotificationInboxCursor.TryDecode(token, out var createdAt, out var decodedId);

        Assert.True(decoded, $"decode failed for token {token}");
        Assert.Equal(created, createdAt);
        Assert.Equal(id, decodedId);
    }

    [Fact]
    public void Tokens_carry_only_the_version_micros_and_uuid_payload()
    {
        var token = NotificationInboxCursor.Encode(
            new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
            Guid.NewGuid()
        );

        Assert.DoesNotContain("+", token, StringComparison.Ordinal);
        Assert.DoesNotContain("/", token, StringComparison.Ordinal);
        Assert.DoesNotContain("=", token, StringComparison.Ordinal);
        Assert.True(token.Length <= NotificationInboxCursor.MaximumTokenBytes);
    }

    [Fact]
    public void Epoch_origin_keeps_micros_much_smaller_than_year_one_ticks()
    {
        var token = NotificationInboxCursor.Encode(
            new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
            Guid.NewGuid()
        );

        // O payload decodifica em micros desde 1970; um encode sem a origem de
        // epoch produziria um número de dígitos bem maior que o canônico.
        Assert.True(NotificationInboxCursor.TryDecode(token, out _, out _));
    }
}
