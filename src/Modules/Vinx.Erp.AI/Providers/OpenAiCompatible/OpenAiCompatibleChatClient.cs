using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace Vinx.Erp.AI.Providers.OpenAiCompatible;

/// <summary>
/// Adapter genérico para qualquer endpoint OpenAI-compatible aprovado, atrás
/// de <see cref="IChatClient"/>. Endpoint, modelo, limites e modalidades vêm
/// do perfil server-side validado na construção. Converte protocolo e devolve
/// resposta normalizada ou proposta de tool: não autoriza, não escolhe
/// empresa, não invoca tool, não persiste conversa e não faz retry. O
/// HttpClient vem do HttpClientFactory do host; o registro DI fica na fatia
/// da API (#138).
/// </summary>
public sealed class OpenAiCompatibleChatClient : IChatClient
{
    private const string DefaultModule = "ai/openai-compatible";
    private const string EventTemplate = "AI adapter {Module} {Result} {Reason} {HttpStatus} {ElapsedMs}";

    // Snapshot imutável do perfil aprovado: mutação das options após a construção
    // não troca endpoint, modelo ou limites em runtime.
    private readonly OpenAiCompatibleOptions _profile;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly TimeSpan _timeout;
    private readonly string _module;
    private readonly ILogger _logger;
    private readonly OpenAIClient _openAIClient;
    private readonly IChatClient _inner;

    public OpenAiCompatibleChatClient(OpenAiCompatibleOptions profile, HttpClient httpClient, ILogger? logger = null)
        : this(profile, null, httpClient, logger, requiresProfileApiKey: true)
    {
    }

    internal OpenAiCompatibleChatClient(
        OpenAiCompatibleOptions profile,
        AgentProviderCredential credential,
        HttpClient httpClient,
        ILogger? logger = null)
        : this(profile, RequireCredential(credential), httpClient, logger, requiresProfileApiKey: false)
    {
    }

    /// <summary>
    /// Constrói o adapter com a credencial exclusiva do ambiente do processo,
    /// sem expor nem copiar o valor para options bindable do host.
    /// </summary>
    public static OpenAiCompatibleChatClient CreateForServer(
        OpenAiCompatibleOptions profile,
        Func<string, string?> readEnvironment,
        HttpClient httpClient,
        ILogger? logger = null)
    {
        var credential = AgentProviderCredential.Resolve(readEnvironment)
            ?? throw new ProviderProfileRejectedException("OpenAI-compatible provider credential is not configured");
        return new OpenAiCompatibleChatClient(profile, credential, httpClient, logger);
    }

    /// <summary>Indica somente se a credencial dedicada está presente e válida.</summary>
    public static bool HasServerCredential(Func<string, string?> readEnvironment) =>
        AgentProviderCredential.Resolve(readEnvironment) is not null;

    private OpenAiCompatibleChatClient(
        OpenAiCompatibleOptions profile,
        AgentProviderCredential? runtimeCredential,
        HttpClient httpClient,
        ILogger? logger,
        bool requiresProfileApiKey)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(httpClient);

        _profile = new OpenAiCompatibleOptions
        {
            Endpoint = profile.Endpoint,
            ApiKey = profile.ApiKey,
            Model = profile.Model,
            Timeout = profile.Timeout,
            MaxMessages = profile.MaxMessages,
            MaxInputCharacters = profile.MaxInputCharacters,
            MaxOutputTokens = profile.MaxOutputTokens,
            MaxTotalTokensPerCall = profile.MaxTotalTokensPerCall,
            AllowImageInputs = profile.AllowImageInputs,
            MaxImagesPerRequest = profile.MaxImagesPerRequest,
            MaxImageBytesPerImage = profile.MaxImageBytesPerImage,
            MaxImageBytesPerRequest = profile.MaxImageBytesPerRequest,
            EstimatedTokensPerImage = profile.EstimatedTokensPerImage,
            ApprovedHosts = profile.ApprovedHosts,
            ApprovedPort = profile.ApprovedPort,
            ModuleTag = profile.ModuleTag,
        };
        _endpoint = _profile.Endpoint
            ?? throw new ProviderEgressRejectedException("provider endpoint is required");
        _model = _profile.Model;
        _timeout = _profile.Timeout;
        _module = string.IsNullOrWhiteSpace(_profile.ModuleTag) ? DefaultModule : _profile.ModuleTag;
        _logger = logger ?? NullLogger.Instance;

        // Endurecimento de egress na fronteira: sem política aprovada, nada sai.
        var egressFailures = ProviderEgressPolicy.ValidateEndpoint(
            _profile.Endpoint, _profile.ApprovedHosts, _profile.ApprovedPort);
        if (egressFailures.Count > 0)
        {
            throw new ProviderEgressRejectedException("provider endpoint is not approved for egress");
        }

        // Validação completa do perfil na construção (o Validator completo é o
        // mesmo que o host registrará com ValidateOnStart em #138).
        var validator = new OpenAiCompatibleOptions.Validator();
        var profileValidation = requiresProfileApiKey
            ? validator.Validate(nameof(OpenAiCompatibleOptions), _profile)
            : validator.ValidateForRuntimeCredential(nameof(OpenAiCompatibleOptions), _profile);
        if (profileValidation.Failed)
        {
            throw new ProviderProfileRejectedException("OpenAI-compatible profile is not approved");
        }

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = _endpoint,
            Transport = new HttpClientPipelineTransport(httpClient),
            // Sem retry automático: falha externa permanece falha/abstenção.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };
        var apiKeyCredential = runtimeCredential?.ToApiKeyCredential() ?? new ApiKeyCredential(_profile.ApiKey);
        _openAIClient = new OpenAIClient(apiKeyCredential, clientOptions);
        _inner = _openAIClient.GetChatClient(_model).AsIChatClient();
    }

    private static AgentProviderCredential RequireCredential(AgentProviderCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return credential;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Validação do perfil antes de qualquer chamada HTTP.
        var mapped = OpenAiCompatibleRequestMapper.Map(_profile, messages, options);

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
            _ = OpenAiCompatibleResponseMapper.ToResult(response, correlationId);
            response.AdditionalProperties ??= [];
            response.AdditionalProperties["CorrelationId"] = correlationId;
            return response;
        }
        catch (Exception exception) when (
            exception is not OpenAiCompatibleProviderException
            and not OpenAiCompatibleRequestValidationException)
        {
            var kind = OpenAiCompatibleResponseMapper.ClassifyFailure(exception, cancellationToken);
            var providerFailure = exception as ClientResultException;
            stopwatch.Stop();
            LogFailure(kind, providerFailure?.Status, stopwatch.Elapsed.TotalMilliseconds);
            // Sem InnerException: a causa bruta pode carregar body/prompt do
            // provider e Exception.ToString() percorre a cadeia inteira.
            throw new OpenAiCompatibleProviderException(kind, providerFailure?.Status);
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
        throw new NotSupportedException("streaming is outside the approved profile");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(OpenAiCompatibleChatClient) || serviceType == typeof(IChatClient) ? this : null;

    public void Dispose()
    {
        // O HttpClient é do factory do host; o adapter descarta somente o que cria.
        if (_openAIClient is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void LogFailure(OpenAiCompatibleFailureKind kind, int? statusCode, double elapsedMs)
    {
        // Cancelamento do chamador é esperado e não gera log. Nenhum template
        // recebe body, prompt, header, chave ou resposta bruta.
        if (kind == OpenAiCompatibleFailureKind.Cancelled)
        {
            return;
        }

        var reason = Reason(kind);
        if (kind is OpenAiCompatibleFailureKind.Timeout or OpenAiCompatibleFailureKind.RateLimited)
        {
            _logger.LogWarning(EventTemplate, _module, "failed", reason, statusCode, elapsedMs);
        }
        else
        {
            _logger.LogError(EventTemplate, _module, "failed", reason, statusCode, elapsedMs);
        }
    }

    private static string Reason(OpenAiCompatibleFailureKind kind) => kind switch
    {
        OpenAiCompatibleFailureKind.Timeout => "timeout",
        OpenAiCompatibleFailureKind.Cancelled => "cancelled",
        OpenAiCompatibleFailureKind.RateLimited => "rate_limited",
        OpenAiCompatibleFailureKind.ProviderInvalid => "provider_invalid",
        _ => "provider_unavailable",
    };
}
