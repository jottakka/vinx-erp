using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Vinx.Erp.ApiHost.Constants;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Notifications.Providers.Meta;

namespace Vinx.Erp.ApiHost.Notifications;

/// <summary>
/// Webhook público de status do WhatsApp Cloud API: challenge de verificação
/// no GET e, no POST, assinatura verificada sobre os bytes brutos antes de
/// qualquer parse, gravação deduplicada por (message_id, event_type) e
/// nenhuma correlação com o negócio.
/// </summary>
public static class MetaWhatsAppWebhookEndpoints
{
    private const int MaximumBodyBytes = ApiConstants.MaximumJsonBodyBytes;

    public static IEndpointRouteBuilder MapMetaWhatsAppWebhookEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var group = app
            .MapGroup("/foundation/notifications/whatsapp/webhook")
            .AllowAnonymous();
        group.MapGet("", VerifyWebhookAsync);
        group.MapPost("", ReceiveWebhookAsync);
        return app;
    }

    private static async Task<IResult> VerifyWebhookAsync(HttpRequest request)
    {
        var options = request.HttpContext.RequestServices.GetRequiredService<MetaWhatsAppOptions>();
        if (!options.WebhookIsConfigured)
        {
            return Unconfigured();
        }

        var mode = request.Query["hub.mode"].ToString();
        var token = request.Query["hub.verify_token"].ToString();
        var challenge = request.Query["hub.challenge"].ToString();
        if (
            !string.Equals(mode, "subscribe", StringComparison.Ordinal)
            || string.IsNullOrEmpty(challenge)
            || !CryptographicOperationsFixedTimeEquals(token, options.WebhookVerifyToken)
        )
        {
            return Results.Forbid();
        }

        return Results.Text(challenge, "text/plain");
    }

    private static IResult Unconfigured() =>
        Results.Json(
            new { code = "whatsapp_unconfigured" },
            statusCode: StatusCodes.Status503ServiceUnavailable
        );

    private static async Task<IResult> ReceiveWebhookAsync(HttpRequest request)
    {
        var earlyOptions = request.HttpContext.RequestServices.GetRequiredService<MetaWhatsAppOptions>();
        if (!earlyOptions.WebhookIsConfigured)
        {
            return Unconfigured();
        }

        byte[] rawBody;
        using (var buffer = new MemoryStream())
        {
            await request.Body.CopyToAsync(buffer);
            if (buffer.Length > MaximumBodyBytes)
            {
                return Results.Json(
                    new { code = "payload_too_large" },
                    statusCode: StatusCodes.Status413PayloadTooLarge
                );
            }

            rawBody = buffer.ToArray();
        }

        var options = request.HttpContext.RequestServices.GetRequiredService<MetaWhatsAppOptions>();
        if (
            !MetaWhatsAppWebhookVerifier.Verify(
                rawBody,
                request.Headers["X-Hub-Signature-256"].ToString(),
                options.AppSecret
            )
        )
        {
            return Results.Json(
                new { code = "invalid_signature" },
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        List<ProviderStatus>? statuses;
        InboundWhatsAppMedia? inboundMedia;
        try
        {
            statuses = ParseStatuses(rawBody);
            inboundMedia = ParseInboundMedia(rawBody);
        }
        catch (JsonException)
        {
            return Results.Json(
                new { code = "invalid_request" },
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var database = request.HttpContext.RequestServices.GetRequiredService<FoundationDbContext>();
        foreach (var status in statuses)
        {
            await RecordAsync(database, status, request.HttpContext.RequestAborted);
        }

        if (inboundMedia.Images.Count > 0 || inboundMedia.Documents.Count > 0)
        {
            // Somente mídia inbound (imagem/documento, #272); demais tipos
            // seguem ignorados.
            var ingress = request.HttpContext.RequestServices.GetRequiredService<
                InboundWhatsAppIngressService
            >();
            foreach (var image in inboundMedia.Images)
            {
                await ingress.RecordAsync(image, request.HttpContext.RequestAborted);
            }
            foreach (var document in inboundMedia.Documents)
            {
                await ingress.RecordAsync(document, request.HttpContext.RequestAborted);
            }
        }

        return Results.Json(new { code = "accepted" });
    }

    private sealed record InboundWhatsAppMedia(
        List<InboundWhatsAppImage> Images,
        List<InboundWhatsAppDocument> Documents
    );

    private static InboundWhatsAppMedia ParseInboundMedia(byte[] rawBody)
    {
        var images = new List<InboundWhatsAppImage>();
        var documents = new List<InboundWhatsAppDocument>();
        using var document = JsonDocument.Parse(rawBody);
        if (!document.RootElement.TryGetProperty("entry", out var entries))
        {
            return new InboundWhatsAppMedia(images, documents);
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes))
            {
                continue;
            }

            foreach (var change in changes.EnumerateArray())
            {
                if (
                    !change.TryGetProperty("value", out var value)
                    || !value.TryGetProperty("messages", out var messages)
                )
                {
                    continue;
                }

                // Canal estável do value: sem phone_number_id a mídia não
                // avança (identidade e idempotência dependem do canal).
                string? phoneNumberId = null;
                if (
                    value.TryGetProperty("metadata", out var metadata)
                    && metadata.TryGetProperty("phone_number_id", out var phoneNumberIdElement)
                    && phoneNumberIdElement.ValueKind == JsonValueKind.String
                    && phoneNumberIdElement.GetString() is { Length: > 0 } rawPhoneNumberId
                )
                {
                    phoneNumberId = rawPhoneNumberId.Trim();
                }

                foreach (var message in messages.EnumerateArray())
                {
                    if (
                        !message.TryGetProperty("type", out var type)
                        || type.GetString() is not ("image" or "document")
                        || !message.TryGetProperty("id", out var id)
                        || id.GetString() is not { Length: > 0 } messageId
                        || !message.TryGetProperty("from", out var from)
                        || from.GetString() is not { Length: > 0 } waId
                        || phoneNumberId is null
                        || !message.TryGetProperty(type.GetString()!, out var media)
                        || !media.TryGetProperty("id", out var mediaIdElement)
                        || mediaIdElement.GetString() is not { Length: > 0 } mediaId
                    )
                    {
                        continue;
                    }

                    string? caption = null;
                    if (
                        media.TryGetProperty("caption", out var captionElement)
                        && captionElement.ValueKind == JsonValueKind.String
                        && captionElement.GetString() is { Length: > 0 } rawCaption
                    )
                    {
                        var trimmed = rawCaption.Trim();
                        caption = trimmed.Length == 0
                            ? null
                            : trimmed.Length > 1000
                                ? trimmed[..1000]
                                : trimmed;
                    }

                    if (type.GetString() == "image")
                    {
                        images.Add(new InboundWhatsAppImage(messageId, phoneNumberId, waId, mediaId, caption));
                    }
                    else
                    {
                        documents.Add(
                            new InboundWhatsAppDocument(messageId, phoneNumberId, waId, mediaId, caption)
                        );
                    }
                }
            }
        }

        return new InboundWhatsAppMedia(images, documents);
    }

    private static List<ProviderStatus> ParseStatuses(byte[] rawBody)
    {
        using var document = JsonDocument.Parse(rawBody);
        var statuses = new List<ProviderStatus>();
        if (!document.RootElement.TryGetProperty("entry", out var entries))
        {
            return statuses;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes))
            {
                continue;
            }

            foreach (var change in changes.EnumerateArray())
            {
                if (
                    !change.TryGetProperty("value", out var value)
                    || !value.TryGetProperty("statuses", out var valueStatuses)
                )
                {
                    continue;
                }

                foreach (var item in valueStatuses.EnumerateArray())
                {
                    var messageId = item.TryGetProperty("id", out var id)
                        ? id.GetString()
                        : null;
                    var status = item.TryGetProperty("status", out var rawStatus)
                        ? rawStatus.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(status))
                    {
                        continue;
                    }

                    statuses.Add(new ProviderStatus(messageId, status, status));
                }
            }
        }

        return statuses;
    }

    private static async Task RecordAsync(
        FoundationDbContext database,
        ProviderStatus status,
        CancellationToken cancellationToken
    )
    {
        if (status.EventType is not ("sent" or "delivered" or "read" or "failed"))
        {
            // Evento desconhecido ou fora de ordem: ignorado sem erro.
            return;
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        var connection = database.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            "SELECT notifications.record_provider_event(@message_id, @event_type, @status)";
        var messageId = command.CreateParameter();
        messageId.ParameterName = "message_id";
        messageId.Value = status.MessageId;
        command.Parameters.Add(messageId);
        var eventType = command.CreateParameter();
        eventType.ParameterName = "event_type";
        eventType.Value = status.EventType;
        command.Parameters.Add(eventType);
        var statusValue = command.CreateParameter();
        statusValue.ParameterName = "status";
        statusValue.Value = status.Status is null ? DBNull.Value : status.Status;
        command.Parameters.Add(statusValue);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static bool CryptographicOperationsFixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(a),
            System.Text.Encoding.ASCII.GetBytes(b)
        );
    }

    private sealed record ProviderStatus(string MessageId, string EventType, string? Status);
}
