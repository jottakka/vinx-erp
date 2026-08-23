using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Notifications;

namespace Vinx.Erp.Notifications.Providers.Meta;

/// <summary>
/// Client do Graph para mensagens de saída; apenas classifica o resultado —
/// retry e DLQ pertencem ao contrato do worker (#48), nunca a este adapter.
/// </summary>
public sealed class MetaWhatsAppClient(
    HttpClient http,
    MetaWhatsAppOptions options,
    ILogger logger
)
{
    public const string HttpClientName = "meta-whatsapp";

    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web
    );

    /// <summary>
    /// Envia resposta de texto na janela de 24h (#140); devolve o id da
    /// mensagem quando o provider confirma, para o worker finalizar o
    /// processamento inbound de forma idempotente.
    /// </summary>
    public async Task<MetaWhatsAppSendResult> SendTextAsync(
        MetaWhatsAppTextMessage message,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            options.SendEndpoint
        )
        {
            Content = new StringContent(
                MetaWhatsAppReplyMapper.Serialize(message),
                Encoding.UTF8,
                "application/json"
            ),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            options.AccessToken
        );

        try
        {
            using var response = await SendRequestAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.RequestTimeout)
            {
                return new MetaWhatsAppSendResult(NotificationProviderResultKind.Ambiguous, null);
            }

            if ((int)response.StatusCode is >= 500 or 429)
            {
                throw new NotificationProviderTransientException(
                    "Meta WhatsApp API reported a transient failure."
                );
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Meta WhatsApp reply {Operation} {Result} {Reason}",
                    "meta_whatsapp_reply",
                    "rejected",
                    $"http_{(int)response.StatusCode}"
                );
                return new MetaWhatsAppSendResult(NotificationProviderResultKind.Rejected, null);
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(payload);
            if (
                document.RootElement.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array
                && messages.GetArrayLength() > 0
                && messages[0].TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(id.GetString())
            )
            {
                return new MetaWhatsAppSendResult(
                    NotificationProviderResultKind.Delivered,
                    id.GetString()
                );
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // A resposta 2xx malformada não prova a ausência de aceite remoto.
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                exception,
                "Meta WhatsApp reply {Operation} {Result} {Reason}",
                "meta_whatsapp_reply",
                "ambiguous",
                "provider_transport"
            );
            return new MetaWhatsAppSendResult(NotificationProviderResultKind.Ambiguous, null);
        }

        logger.LogWarning(
            "Meta WhatsApp reply {Operation} {Result} {Reason}",
            "meta_whatsapp_reply",
            "ambiguous",
            "malformed_success"
        );
        return new MetaWhatsAppSendResult(NotificationProviderResultKind.Ambiguous, null);
    }

    private async Task<HttpResponseMessage> SendRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout do HttpClient: resultado incerto no provider vira
            // ambíguo terminal, sem retry cego.
            logger.LogWarning(
                exception,
                "Meta WhatsApp send {Operation} {Result} {Reason}",
                "meta_whatsapp_send",
                "ambiguous",
                "provider_timeout"
            );
            throw new InboundWhatsAppReplyTimeoutException();
        }
    }

    public async Task<NotificationProviderResult> SendAsync(
        MetaWhatsAppMessage message,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            options.SendEndpoint
        )
        {
            Content = new StringContent(
                MetaWhatsAppMapper.Serialize(message),
                Encoding.UTF8,
                "application/json"
            ),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            options.AccessToken
        );

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout do HttpClient: resultado incerto no provider vira
            // ambíguo terminal, sem retry cego.
            logger.LogWarning(
                exception,
                "Meta WhatsApp send {Operation} {Result} {Reason}",
                "meta_whatsapp_send",
                "ambiguous",
                "provider_timeout"
            );
            return new NotificationProviderResult(NotificationProviderResultKind.Ambiguous);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestTimeout)
            {
                return new NotificationProviderResult(
                    NotificationProviderResultKind.Ambiguous
                );
            }

            if ((int)response.StatusCode is >= 500 or 429)
            {
                throw new NotificationProviderTransientException(
                    "Meta WhatsApp API reported a transient failure."
                );
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Meta WhatsApp send {Operation} {Result} {Reason}",
                    "meta_whatsapp_send",
                    "rejected",
                    $"http_{(int)response.StatusCode}"
                );
                return new NotificationProviderResult(
                    NotificationProviderResultKind.Rejected
                );
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (
                    document.RootElement.TryGetProperty(
                        "messages",
                        out var messages
                    )
                    && messages.GetArrayLength() > 0
                )
                {
                    return new NotificationProviderResult(
                        NotificationProviderResultKind.Delivered
                    );
                }

                return new NotificationProviderResult(
                    NotificationProviderResultKind.Rejected
                );
            }
            catch (JsonException)
            {
                // Corpo 2xx inválido é ambíguo: o envio pode ter sido aceito.
                return new NotificationProviderResult(
                    NotificationProviderResultKind.Ambiguous
                );
            }
        }
    }
}

/// <summary>
/// Resultado do envio da resposta: classificação estável mais o id atribuído
/// pelo provider quando confirmado (usado só para o finalize idempotente).
/// </summary>
public sealed record MetaWhatsAppSendResult(
    NotificationProviderResultKind Kind,
    string? MessageId
);
