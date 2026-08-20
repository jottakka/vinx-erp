using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Notifications;

namespace Vinx.Erp.Notifications.Providers.Meta;

/// <summary>Resultado estável do download de mídia: bytes + media type.</summary>
public sealed record MetaWhatsAppMedia(byte[] Bytes, string MediaType);

/// <summary>
/// Download de mídia do Graph em duas fases (metadados com Bearer → URL
/// assinada de curta duração → bytes). Transitivo (5xx/429/timeout de rede)
/// vira exceção transitiva; o resto é rejeição determinística — nunca há
/// sucesso inventado.
/// </summary>
public sealed class MetaWhatsAppMediaClient(
    HttpClient http,
    MetaWhatsAppOptions options,
    ILogger logger
)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<MetaWhatsAppMedia> DownloadAsync(
        string mediaId,
        long maximumBytes,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaId);

        var url = await GetMediaUrlAsync(mediaId, cancellationToken);
        return await DownloadBytesAsync(url, maximumBytes, cancellationToken);
    }

    private async Task<string> GetMediaUrlAsync(string mediaId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{options.ApiBaseUrl.TrimEnd('/')}/v22.0/{mediaId.TrimStart('/')}"
        );
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            options.AccessToken
        );

        using var response = await SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        string? url = null;
        string? mediaType = null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (
                document.RootElement.TryGetProperty("url", out var urlElement)
                && urlElement.ValueKind == JsonValueKind.String
            )
            {
                url = urlElement.GetString();
            }

            if (
                document.RootElement.TryGetProperty("mime_type", out var mimeElement)
                && mimeElement.ValueKind == JsonValueKind.String
            )
            {
                mediaType = mimeElement.GetString();
            }
        }
        catch (JsonException)
        {
            throw new InboundWhatsAppPermanentException("media metadata response is not valid JSON");
        }

        if (
            string.IsNullOrWhiteSpace(url)
            || string.IsNullOrWhiteSpace(mediaType)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
        )
        {
            throw new InboundWhatsAppPermanentException("media metadata is incomplete");
        }

        return url;
    }

    private async Task<MetaWhatsAppMedia> DownloadBytesAsync(
        string url,
        long maximumBytes,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken);
        if (
            response.Content.Headers.ContentLength is long declared
            && declared > maximumBytes
        )
        {
            throw new InboundWhatsAppPermanentException("media exceeds the approved byte limit");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > maximumBytes)
        {
            throw new InboundWhatsAppPermanentException("media exceeds the approved byte limit");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType
            ?? "application/octet-stream";
        return new MetaWhatsAppMedia(bytes, mediaType);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new NotificationProviderTransientException("Meta WhatsApp media request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new NotificationProviderTransientException("Meta WhatsApp media request failed.");
        }

        if ((int)response.StatusCode is >= 500 or 429)
        {
            response.Dispose();
            throw new NotificationProviderTransientException("Meta WhatsApp media API reported a transient failure.");
        }

        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            logger.LogWarning(
                "Meta WhatsApp media {Operation} {Result} {Reason}",
                "meta_whatsapp_media",
                "rejected",
                $"http_{(int)response.StatusCode}"
            );
            throw new InboundWhatsAppPermanentException("media was rejected by the provider");
        }

        return response;
    }
}
