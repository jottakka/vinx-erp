using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace Vinx.Erp.AI.Providers.DeepSeek;

/// <summary>
/// Adapter DeepSeek v4 Flash por endpoint OpenAI-compatible aprovado, atrás de
/// <see cref="IChatClient"/>. Converte protocolo e devolve resposta normalizada
/// ou proposta de tool: não autoriza, não escolhe empresa, não invoca tool,
/// não persiste conversa e não faz retry. O HttpClient vem do
/// HttpClientFactory do host; o registro DI fica na fatia da API.
/// </summary>
public sealed class DeepSeekChatClient : IChatClient
{
    private const string Module = "ai/deepseek";
    private const string EventTemplate = "AI adapter " + Module + " {Result} {Reason} {HttpStatus} {ElapsedMs}";

    // Snapshot imutável do perfil aprovado: mutação das options após a construção
    // não troca endpoint, modelo ou limites em runtime.
    private readonly DeepSeekOptions _profile;
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;
    private readonly OpenAIClient _openAIClient;
    private readonly IChatClient _inner;

    public DeepSeekChatClient(DeepSeekOptions profile, HttpClient httpClient, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (profile.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "timeout must be positive");
        }

        _profile = new DeepSeekOptions
        {
            Endpoint = profile.Endpoint,
            ApiKey = profile.ApiKey,
            Model = profile.Model,
            Timeout = profile.Timeout,
            MaxMessages = profile.MaxMessages,
            MaxInputCharacters = profile.MaxInputCharacters,
            MaxOutputTokens = profile.MaxOutputTokens,
            MaxTotalTokensPerCall = profile.MaxTotalTokensPerCall,
        };
        _endpoint = _profile.Endpoint;
        _apiKey = _profile.ApiKey;
        _model = _profile.Model;
        _timeout = _profile.Timeout;
        _logger = logger ?? NullLogger.Instance;

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = _endpoint,
            Transport = new HttpClientPipelineTransport(httpClient),
            // Sem retry automático: falha externa permanece falha/abstenção.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };
        _openAIClient = new OpenAIClient(new ApiKeyCredential(_apiKey), clientOptions);
        _inner = _openAIClient.GetChatClient(_model).AsIChatClient();
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Validação do perfil antes de qualquer chamada HTTP.
        var mapped = DeepSeekRequestMapper.Map(_profile, messages, options);

        var correlationId = Guid.NewGuid().ToString("N");
        using var correlationScope = _logger.BeginScope(
            new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCancellation.CancelAfter(_timeout);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await _inner
                .GetResponseAsync(mapped.Messages, mapped.Options, linkedCancellation.Token)
                .ConfigureAwait(false);

            // Normaliza e valida o schema da resposta antes de devolver.
            _ = DeepSeekResponseMapper.ToResult(response, correlationId);
            response.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            response.AdditionalProperties["CorrelationId"] = correlationId;
            return response;
        }
        catch (Exception exception) when (
            exception is not DeepSeekProviderException
            and not DeepSeekRequestValidationException)
        {
            var kind = DeepSeekResponseMapper.ClassifyFailure(exception, cancellationToken);
            var providerFailure = exception as ClientResultException;
            stopwatch.Stop();
            LogFailure(kind, providerFailure?.Status, stopwatch.Elapsed.TotalMilliseconds);
            throw new DeepSeekProviderException(kind, providerFailure?.Status, exception);
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        string chatMessage,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetResponseAsync([new ChatMessage(ChatRole.User, chatMessage)], options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("streaming is outside the approved DeepSeek profile");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(DeepSeekChatClient) || serviceType == typeof(IChatClient) ? this : null;

    public void Dispose()
    {
        // O HttpClient é do factory do host; o adapter descarta somente o que cria.
        if (_openAIClient is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void LogFailure(DeepSeekFailureKind kind, int? statusCode, double elapsedMs)
    {
        // Cancelamento do chamador é esperado e não gera log. Nenhum template
        // recebe body, prompt, header, chave ou resposta bruta.
        if (kind == DeepSeekFailureKind.Cancelled)
        {
            return;
        }

        var reason = Reason(kind);
        if (kind is DeepSeekFailureKind.Timeout or DeepSeekFailureKind.RateLimited)
        {
            _logger.LogWarning(EventTemplate, "failed", reason, statusCode, elapsedMs);
        }
        else
        {
            _logger.LogError(EventTemplate, "failed", reason, statusCode, elapsedMs);
        }
    }

    private static string Reason(DeepSeekFailureKind kind) => kind switch
    {
        DeepSeekFailureKind.Timeout => "timeout",
        DeepSeekFailureKind.Cancelled => "cancelled",
        DeepSeekFailureKind.RateLimited => "rate_limited",
        DeepSeekFailureKind.ProviderInvalid => "provider_invalid",
        _ => "provider_unavailable",
    };
}
