using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vinx.Erp.Notifications;

namespace Vinx.Erp.Notifications.Providers.Telegram;

/// <summary>
/// Cliente mínimo do Bot API. Não registra request, URI, token, corpo ou
/// descrição retornada pelo provedor; retry pertence ao worker.
/// </summary>
public sealed class TelegramClient(HttpClient http, TelegramOptions options)
{
    public const string HttpClientName = "telegram-bot";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Envia texto aprovado e classifica somente o resultado seguro para o
    /// worker. O token é incorporado à URI apenas durante esta chamada.
    /// </summary>
    public async Task<NotificationProviderResult> SendAsync(
        TelegramMessage message,
        CancellationToken cancellationToken
    )
    {
        if (!options.OutboundIsConfigured)
        {
            return Rejected();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildBotMethodUrl(options, "sendMessage")
        )
        {
            Content = new StringContent(
                JsonSerializer.Serialize(message, SerializerOptions),
                Encoding.UTF8,
                "application/json"
            ),
        };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return Ambiguous();
        }
        catch (HttpRequestException)
        {
            return Ambiguous();
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestTimeout)
            {
                return Ambiguous();
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new NotificationProviderTransientException(
                    "Telegram outbound request failed transiently."
                );
            }

            if (!response.IsSuccessStatusCode)
            {
                return Rejected();
            }

            if (response.Content.Headers.ContentLength is > 8_192)
            {
                return Ambiguous();
            }

            string? payload;
            try
            {
                payload = await ReadBoundedPayloadAsync(response.Content, cancellationToken);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                return Ambiguous();
            }
            catch (HttpRequestException)
            {
                return Ambiguous();
            }
            catch (IOException)
            {
                return Ambiguous();
            }

            if (payload is null || payload.Length > 8_192)
            {
                return Ambiguous();
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (
                    root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("ok", out var ok)
                    && ok.ValueKind == JsonValueKind.False
                )
                {
                    return Rejected();
                }

                if (
                    root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("ok", out ok)
                    && ok.ValueKind == JsonValueKind.True
                    && root.TryGetProperty("result", out var result)
                    && result.ValueKind == JsonValueKind.Object
                    && result.TryGetProperty("message_id", out var messageId)
                    && messageId.ValueKind == JsonValueKind.Number
                    && messageId.TryGetInt64(out var id)
                    && id > 0
                )
                {
                    return Delivered();
                }
            }
            catch (JsonException)
            {
                return Ambiguous();
            }

            return Ambiguous();
        }
    }

    /// <summary>
    /// Lê no máximo 8192 bytes do corpo antes de devolvê-lo como texto; um corpo
    /// maior sinaliza ambiguidade sem ser consumido até o fim.
    /// </summary>
    private static async Task<string?> ReadBoundedPayloadAsync(
        HttpContent content,
        CancellationToken cancellationToken
    )
    {
        await using var buffer = new MemoryStream();
        await using (var stream = await content.ReadAsStreamAsync(cancellationToken))
        {
            var chunk = new byte[8_192];
            while (true)
            {
                var bytesRead = await stream.ReadAsync(chunk, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, bytesRead);
                if (buffer.Length > 8_192)
                {
                    return null;
                }
            }
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static NotificationProviderResult Delivered() =>
        new(NotificationProviderResultKind.Delivered);

    private static NotificationProviderResult Rejected() =>
        new(NotificationProviderResultKind.Rejected);

    private static NotificationProviderResult Ambiguous() =>
        new(NotificationProviderResultKind.Ambiguous);

    internal static string BuildBotMethodUrl(TelegramOptions options, string method)
    {
        if (method is not ("sendMessage" or "getFile"))
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }
        return $"{options.ApiBaseUrl.TrimEnd('/')}/bot{options.BotToken}/{method}";
    }
}

/// <summary>Corpo mínimo permitido pelo Telegram sendMessage.</summary>
public sealed record TelegramMessage(
    [property: JsonPropertyName("chat_id")] string ChatId,
    [property: JsonPropertyName("text")] string Text
);
