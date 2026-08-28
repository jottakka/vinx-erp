using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vinx.Erp.Notifications.Providers.Telegram;

public sealed record TelegramMedia(string MediaType, ReadOnlyMemory<byte> Bytes);

public sealed class InboundTelegramMediaException : Exception
{
    public InboundTelegramMediaException() : base("Telegram media is unavailable.") { }
}

/// <summary>
/// Resolve e baixa somente paths de arquivo emitidos pelo Bot API, com teto
/// estrito e sem registrar token, file_id, path, conteúdo ou resposta remota.
/// </summary>
public sealed class TelegramMediaClient(HttpClient http, TelegramOptions options)
{
    private const int MaximumMetadataBytes = 8_192;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<TelegramMedia> DownloadAsync(
        string fileId,
        string expectedMediaType,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!options.OutboundIsConfigured
            || !IsSafeFileId(fileId)
            || !IsAllowedMediaType(expectedMediaType)
            || maximumBytes <= 0)
        {
            throw new InboundTelegramMediaException();
        }

        var filePath = await ResolveFilePathAsync(fileId, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{options.ApiBaseUrl.TrimEnd('/')}/file/bot{options.BotToken}/{filePath}");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentLength is { } advertised && advertised > maximumBytes
            || !string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                expectedMediaType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InboundTelegramMediaException();
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(16_384, maximumBytes + 1)];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > maximumBytes)
            {
                throw new InboundTelegramMediaException();
            }
            buffer.Write(chunk, 0, read);
        }

        return buffer.Length > 0
            ? new TelegramMedia(expectedMediaType, buffer.ToArray())
            : throw new InboundTelegramMediaException();
    }

    private async Task<string> ResolveFilePathAsync(
        string fileId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            TelegramClient.BuildBotMethodUrl(options, "getFile"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new GetFileRequest(fileId), SerializerOptions),
                Encoding.UTF8,
                "application/json"),
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentLength is > MaximumMetadataBytes)
        {
            throw new InboundTelegramMediaException();
        }

        var payload = await ReadBoundedAsync(
            response.Content,
            MaximumMetadataBytes,
            cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("file_path", out var path)
                && path.ValueKind == JsonValueKind.String
                && path.GetString() is { } candidate
                && IsAllowedProviderFilePath(candidate))
            {
                return candidate;
            }
        }
        catch (JsonException)
        {
            throw new InboundTelegramMediaException();
        }

        throw new InboundTelegramMediaException();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500)
            {
                response.Dispose();
                throw new NotificationProviderTransientException(
                    "Telegram media request failed transiently.");
            }
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NotificationProviderTransientException(
                "Telegram media request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new NotificationProviderTransientException(
                "Telegram media request failed transiently.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(4_096, maximumBytes + 1)];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }
            if (buffer.Length + read > maximumBytes)
            {
                throw new InboundTelegramMediaException();
            }
            buffer.Write(chunk, 0, read);
        }
    }

    internal static bool IsAllowedProviderFilePath(string path)
    {
        var slash = path.IndexOf('/');
        if (slash <= 0
            || slash != path.LastIndexOf('/')
            || path.Length is < 3 or > 240
            || path.Contains("..", StringComparison.Ordinal)
            || path[..slash] is not ("documents" or "photos"))
        {
            return false;
        }

        var fileName = path[(slash + 1)..];
        return fileName.Length is >= 1 and <= 200
            && fileName.All(character => character is (>= 'A' and <= 'Z')
                or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '_' or '-' or '.');
    }

    private static bool IsSafeFileId(string fileId) =>
        fileId.Length is >= 1 and <= 512
        && fileId.All(character => character is (>= 'A' and <= 'Z')
            or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');

    private static bool IsAllowedMediaType(string mediaType) =>
        mediaType is "application/pdf" or "image/jpeg" or "image/png";

    private sealed record GetFileRequest(
        [property: JsonPropertyName("file_id")] string FileId);
}
