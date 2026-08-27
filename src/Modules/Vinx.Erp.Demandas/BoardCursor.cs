using System.Text;


namespace Vinx.Erp.Demandas;

/// <summary>Cursor opaco do quadro: instante + id, base64url sem padding.</summary>
public static class BoardCursor
{
    public static string Encode(DateTimeOffset occurredAt, Guid id)
    {
        var payload = $"{occurredAt.UtcTicks:D19}:{id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? value, out (DateTimeOffset OccurredAt, Guid Id) decoded)
    {
        decoded = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            var remainder = padded.Length % 4;
            if (remainder == 2)
            {
                padded += "==";
            }
            else if (remainder == 3)
            {
                padded += "=";
            }
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(padded);
            }
            catch (ArgumentException)
            {
                return false;
            }

            var payload = Encoding.UTF8.GetString(bytes);
            var separator = payload.IndexOf(':', StringComparison.Ordinal);
            if (separator is not 19 || !long.TryParse(payload[..separator], out var ticks))
            {
                return false;
            }

            if (!Guid.TryParseExact(payload[(separator + 1)..], "N", out var id) || id == Guid.Empty)
            {
                return false;
            }

            decoded = (new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
