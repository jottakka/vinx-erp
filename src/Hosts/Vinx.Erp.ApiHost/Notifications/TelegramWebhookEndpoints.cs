using System.Text;
using System.Text.Json;
using Vinx.Erp.ApiHost.Constants;
using Vinx.Erp.Notifications.Providers.Telegram;

namespace Vinx.Erp.ApiHost.Notifications;

/// <summary>
/// Texto privado inbound do Telegram que já passou pela autenticação do
/// webhook e pelo allowlist semântico do transporte.
/// </summary>
public sealed record InboundTelegramText(long UpdateId, long ChatId, long MessageId, string Text);

/// <summary>
/// Webhook público do Telegram: autentica o único header de segredo antes de
/// ler ou interpretar o corpo e encaminha somente texto de conversa privada.
/// </summary>
public static class TelegramWebhookEndpoints
{
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    private const int MaximumBodyBytes = ApiConstants.MaximumJsonBodyBytes;
    private static readonly char[] SqlBtrimCharacters = [' ', '\t', '\n', '\v', '\f', '\r'];

    /// <summary>
    /// Mapeia o POST público e recebe uma callback que resolve o ingress no
    /// escopo da requisição, preservando o endpoint independente do serviço.
    /// </summary>
    public static IEndpointRouteBuilder MapTelegramWebhookEndpoints(
        this IEndpointRouteBuilder app,
        Func<IServiceProvider, InboundTelegramText, CancellationToken, Task> ingress
    )
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(ingress);

        app.MapPost(
                "/foundation/notifications/telegram/webhook",
                (Func<HttpRequest, Task<IResult>>)(request => ReceiveWebhookAsync(request, ingress))
            )
            .AllowAnonymous();
        return app;
    }

    private static async Task<IResult> ReceiveWebhookAsync(
        HttpRequest request,
        Func<IServiceProvider, InboundTelegramText, CancellationToken, Task> ingress
    )
    {
        var options = request.HttpContext.RequestServices.GetRequiredService<TelegramOptions>();
        if (!options.WebhookIsConfigured)
        {
            return Results.Json(
                new { code = "telegram_unconfigured" },
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
        }

        if (
            !request.Headers.TryGetValue(SecretHeader, out var secretTokens)
            || secretTokens.Count != 1
            || !TelegramWebhookVerifier.Verify(secretTokens[0], options.WebhookSecretToken)
        )
        {
            return Results.Json(
                new { code = "invalid_secret" },
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        if (!request.HasJsonContentType())
        {
            return Results.Json(
                new { code = "unsupported_media_type" },
                statusCode: StatusCodes.Status415UnsupportedMediaType
            );
        }

        var buffer = new byte[MaximumBodyBytes + 1];
        var bodyLength = 0;
        try
        {
            int bytesRead;
            do
            {
                bytesRead = await request.Body.ReadAsync(
                    buffer.AsMemory(bodyLength),
                    request.HttpContext.RequestAborted
                );
                bodyLength += bytesRead;
            }
            while (bytesRead != 0 && bodyLength <= MaximumBodyBytes);
        }
        catch (IOException)
        {
            return InvalidRequest();
        }

        if (bodyLength > MaximumBodyBytes)
        {
            return Results.Json(
                new { code = "payload_too_large" },
                statusCode: StatusCodes.Status413PayloadTooLarge
            );
        }

        InboundTelegramText? inbound;
        try
        {
            inbound = ParsePrivateText(buffer.AsMemory(0, bodyLength));
        }
        catch (JsonException)
        {
            return InvalidRequest();
        }

        if (inbound is not null)
        {
            await ingress(
                request.HttpContext.RequestServices,
                inbound,
                request.HttpContext.RequestAborted
            );
        }

        return Results.Json(new { code = "accepted" });
    }

    private static IResult InvalidRequest() =>
        Results.Json(
            new { code = "invalid_request" },
            statusCode: StatusCodes.Status400BadRequest
        );

    private static InboundTelegramText? ParsePrivateText(ReadOnlyMemory<byte> rawBody)
    {
        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;
        if (
            root.ValueKind != JsonValueKind.Object
            || !TryGetPositiveId(root, "update_id", out var updateId)
            || !root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !TryGetPositiveId(message, "message_id", out var messageId)
            || !message.TryGetProperty("from", out var from)
            || from.ValueKind != JsonValueKind.Object
            || !TryGetPositiveId(from, "id", out var fromId)
            || !from.TryGetProperty("is_bot", out var isBot)
            || isBot.ValueKind != JsonValueKind.False
            || !message.TryGetProperty("chat", out var chat)
            || chat.ValueKind != JsonValueKind.Object
            || !TryGetPositiveId(chat, "id", out var chatId)
            || !chat.TryGetProperty("type", out var chatType)
            || chatType.ValueKind != JsonValueKind.String
            || !string.Equals(chatType.GetString(), "private", StringComparison.Ordinal)
            || fromId != chatId
            || !message.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.String
            || text.GetString() is not { } textBody
            || !HasValidText(textBody)
        )
        {
            return null;
        }

        return new InboundTelegramText(updateId, chatId, messageId, textBody);
    }

    private static bool TryGetPositiveId(JsonElement parent, string propertyName, out long value)
    {
        value = 0;
        return parent.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value)
            && value > 0;
    }

    private static bool HasValidText(string text) =>
        text.Trim(SqlBtrimCharacters).Length > 0
        && text.EnumerateRunes().Count() <= 4096;
}
