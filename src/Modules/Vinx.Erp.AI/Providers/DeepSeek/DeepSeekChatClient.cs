using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Vinx.Erp.AI.Providers.OpenAiCompatible;

namespace Vinx.Erp.AI.Providers.DeepSeek;

/// <summary>
/// Adapter do perfil DeepSeek v4 Flash: fachada fina sobre o núcleo
/// <see cref="OpenAiCompatibleChatClient"/> que preserva o contrato público
/// do #135-A (exceções e tipos DeepSeek) e o rótulo de logging
/// `ai/deepseek`. Não autoriza, não escolhe empresa, não invoca tool, não
/// persiste conversa e não faz retry.
/// </summary>
public sealed class DeepSeekChatClient : IChatClient
{
    private readonly OpenAiCompatibleChatClient _inner;

    public DeepSeekChatClient(DeepSeekOptions profile, HttpClient httpClient, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(httpClient);

        _inner = new OpenAiCompatibleChatClient(profile.ToOpenAiCompatibleProfile(), httpClient, logger);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        try
        {
            return await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenAiCompatibleRequestValidationException exception)
        {
            throw new DeepSeekRequestValidationException(exception.Message);
        }
        catch (OpenAiCompatibleProviderException exception)
        {
            // Sem inner: a cadeia pública nunca carrega a causa bruta do provider.
            throw new DeepSeekProviderException(Translate(exception.Kind), exception.HttpStatusCode);
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

    public void Dispose() => _inner.Dispose();

    internal static DeepSeekFailureKind Translate(OpenAiCompatibleFailureKind kind) => kind switch
    {
        OpenAiCompatibleFailureKind.Timeout => DeepSeekFailureKind.Timeout,
        OpenAiCompatibleFailureKind.Cancelled => DeepSeekFailureKind.Cancelled,
        OpenAiCompatibleFailureKind.RateLimited => DeepSeekFailureKind.RateLimited,
        OpenAiCompatibleFailureKind.ProviderInvalid => DeepSeekFailureKind.ProviderInvalid,
        _ => DeepSeekFailureKind.ProviderUnavailable,
    };
}
