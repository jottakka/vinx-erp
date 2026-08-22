using System.Net;
using System.Text;
using System.Text.Json;
using Vinx.Erp.Notifications.Providers.Meta;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Valores de teste sem valor real (nenhum segredo verdadeiro vive no
/// repositório): perfis completos exigidos pelo fail-closed do inbound.
/// </summary>
internal static class InboundAiTestProfiles
{
    public const string PlaceholderKey = "test-ai-placeholder";

    public const string TextModel = "test-text-model";

    public const string VisionModel = "test-vision-model";

    public static InboundWhatsAppAiOptions Create() =>
        new()
        {
            Text = new()
            {
                Endpoint = new Uri("https://ai-text.example.test/v1"),
                ApiKey = PlaceholderKey,
                Model = TextModel,
            },
            Vision = new()
            {
                Endpoint = new Uri("https://ai-vision.example.test/v1"),
                ApiKey = PlaceholderKey,
                Model = VisionModel,
                AllowImageInputs = true,
            },
        };
}

public sealed record InboundCapturedRequest(
    string Body,
    System.Net.Http.Headers.AuthenticationHeaderValue? Authorization
);

public sealed record FakeMediaObject(byte[] Bytes, string ContentType);

/// <summary>
/// Handler falso do Graph para o fluxo inbound: metadados de mídia, download
/// da mídia (PNG 1x1 válido para o canonicalizador, ou objeto registrado por
/// id no dicionário para documentos) e o POST da resposta.
/// Tipos públicos concretos: o codegen do Wolverine só inline-a dependências
/// públicas tipadas.
/// </summary>
public sealed class FakeInboundMetaHandler : HttpMessageHandler
{
    private const string MediaUrlPrefix = "https://media.example.test/";

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="
    );

    private readonly Lock _gate = new();
    private readonly List<InboundCapturedRequest> _replyRequests = [];
    private readonly Dictionary<string, FakeMediaObject> _mediaObjects = new(StringComparer.Ordinal);

    public bool FailMediaMetadata { get; set; }

    public bool FailFirstDownloadOnce { get; set; }

    public int MediaMetadataGets { get; private set; }

    public int MediaDownloads { get; private set; }

    public int ReplyPosts => _replyRequests.Count;

    public IReadOnlyList<InboundCapturedRequest> ReplyRequests
    {
        get
        {
            lock (_gate)
            {
                return _replyRequests.ToList();
            }
        }
    }

    public void AddMedia(string mediaId, byte[] bytes, string contentType) =>
        _mediaObjects[mediaId] = new FakeMediaObject(bytes, contentType);

    private FakeMediaObject? FindMedia(Uri requestUri)
    {
        var name = requestUri.Segments.Length > 0
            ? requestUri.Segments[^1].TrimEnd('/')
            : string.Empty;
        return _mediaObjects.TryGetValue(name, out var media) ? media : null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (request.Method == HttpMethod.Get && request.RequestUri?.Host == "media.example.test")
        {
            MediaDownloads++;
            if (FailFirstDownloadOnce && MediaDownloads == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            var media = FindMedia(request.RequestUri);
            var content = new ByteArrayContent(media?.Bytes ?? PngBytes);
            if (media is not null)
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    media.ContentType
                );
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        if (request.Method == HttpMethod.Get)
        {
            MediaMetadataGets++;
            if (FailMediaMetadata)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var mediaId = request.RequestUri?.Segments is { Length: > 0 } segments
                ? segments[^1].TrimEnd('/')
                : "1938";
            var media = _mediaObjects.GetValueOrDefault(mediaId);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"url":"{{MediaUrlPrefix}}{{mediaId}}","mime_type":"{{media?.ContentType ?? "image/png"}}","id":"{{mediaId}}"}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        }

        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate)
        {
            _replyRequests.Add(new InboundCapturedRequest(body, request.Headers.Authorization));
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"messaging_product":"whatsapp","messages":[{"id":"wamid.reply.test.1"}]}""",
                Encoding.UTF8,
                "application/json"
            ),
        };
    }
}

/// <summary>
/// Handler falso de endpoint OpenAI-compatible: responde chat completion com
/// o conteúdo configurado (JSON estrito do OCR no perfil de visão, texto da
/// resposta no perfil de texto) e captura os corpos para asserção.
/// </summary>
public sealed class FakeInboundAiHandler(HttpStatusCode status, string assistantContent)
    : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<string> _requestBodies = [];

    public IReadOnlyList<string> RequestBodies
    {
        get
        {
            lock (_gate)
            {
                return _requestBodies.ToList();
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate)
        {
            _requestBodies.Add(body);
        }

        var completion = JsonSerializer.Serialize(
            new
            {
                id = "chatcmpl-test",
                @object = "chat.completion",
                created = 1L,
                model = "test-model",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = assistantContent },
                        finish_reason = "stop",
                    },
                },
                usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
            }
        );
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(completion, Encoding.UTF8, "application/json"),
        };
    }
}
