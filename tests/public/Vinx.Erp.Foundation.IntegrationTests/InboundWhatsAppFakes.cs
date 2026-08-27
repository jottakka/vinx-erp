using System.Net;
using System.Text;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Notifications.Providers.Meta;

namespace Vinx.Erp.Foundation.IntegrationTests;

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

    public Action? ReplyAccepted { get; set; }

    public HttpStatusCode ReplyStatusCode { get; set; } = HttpStatusCode.OK;

    public string ReplyResponseBody { get; set; } =
        """{"messaging_product":"whatsapp","messages":[{"id":"wamid.reply.test.1"}]}""";

    public Exception? ReplyException { get; set; }

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
        ReplyAccepted?.Invoke();

        if (ReplyException is not null)
        {
            throw ReplyException;
        }

        return new HttpResponseMessage(ReplyStatusCode)
        {
            Content = new StringContent(
                ReplyResponseBody,
                Encoding.UTF8,
                "application/json"
            ),
        };
    }
}

/// <summary>
/// Falha uma confirmação local depois de o fake registrar o aceite do provider.
/// Modela a perda de processo antes da conclusão durável, sem alterar o adapter.
/// </summary>
public sealed class OneShotTransactionCommitFailureInterceptor : DbTransactionInterceptor
{
    private int _state;

    public void Arm() => Interlocked.CompareExchange(ref _state, 1, 0);

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default
    )
    {
        if (Interlocked.CompareExchange(ref _state, 2, 1) == 1)
        {
            throw new InvalidOperationException("Test-only reply completion failure.");
        }

        return ValueTask.FromResult(result);
    }
}

public sealed record CapturedLogEntry(
    LogLevel Level,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties
);

public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<CapturedLogEntry> _entries = [];

    public IReadOnlyList<CapturedLogEntry> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal)
            : new Dictionary<string, object?>();
        _entries.Add(new(logLevel, formatter(state, exception), exception, properties));
    }
}
