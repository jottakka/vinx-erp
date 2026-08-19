using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Vinx.Erp.AI.Providers.DeepSeek;

/// <summary>Classificação segura da falha externa; nunca vira sucesso inventado.</summary>
public enum DeepSeekFailureKind
{
    Timeout,
    Cancelled,
    RateLimited,
    ProviderInvalid,
    ProviderUnavailable,
}

/// <summary>
/// Falha classificada do provider. A mensagem é fixa e redacted: nunca contém
/// body, prompt, header, chave ou resposta bruta; a exceção original segue
/// preservada como inner para o ponto que decide a falha.
/// </summary>
public sealed class DeepSeekProviderException : Exception
{
    public DeepSeekProviderException(DeepSeekFailureKind kind, int? httpStatusCode = null, Exception? innerException = null)
        : base(FormatMessage(kind, httpStatusCode), innerException)
    {
        Kind = kind;
        HttpStatusCode = httpStatusCode;
    }

    public DeepSeekFailureKind Kind { get; }

    public int? HttpStatusCode { get; }

    private static string FormatMessage(DeepSeekFailureKind kind, int? httpStatusCode) =>
        httpStatusCode is int status
            ? $"DeepSeek provider call failed: {kind.ToString().ToLowerInvariant()} ({status})"
            : $"DeepSeek provider call failed: {kind.ToString().ToLowerInvariant()}";
}

/// <summary>Consumo de tokens reportado para o orçamento do runtime.</summary>
public sealed record DeepSeekUsage(long? InputTokens, long? OutputTokens, long? TotalTokens);

/// <summary>Proposta de leitura tipada; o adapter nunca a executa.</summary>
public sealed record DeepSeekToolProposal(
    string CallId,
    string Name,
    IDictionary<string, object?> Arguments);

/// <summary>
/// Resposta normalizada: decisão final tipada ou proposta de tool, com
/// correlation ID da execução e consumo para o orçamento.
/// </summary>
public sealed record DeepSeekChatResult(
    string CorrelationId,
    string? FinalText,
    IReadOnlyList<DeepSeekToolProposal> ToolProposals,
    string? FinishReason,
    DeepSeekUsage Usage)
{
    public bool HasToolProposals => ToolProposals.Count > 0;
}

/// <summary>
/// Converte a resposta do provider no contrato normalizado e classifica falhas
/// externas. Campos desconhecidos são ignorados; schema inválido de tool vira
/// falha provider_invalid, nunca resultado inventado.
/// </summary>
public static class DeepSeekResponseMapper
{
    public static DeepSeekChatResult ToResult(ChatResponse? response, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var proposals = new List<DeepSeekToolProposal>();
        foreach (var functionCall in response.Messages.SelectMany(message => message.Contents)
                     .OfType<FunctionCallContent>())
        {
            if (functionCall.Exception is not null)
            {
                throw new DeepSeekProviderException(
                    DeepSeekFailureKind.ProviderInvalid, null, functionCall.Exception);
            }

            proposals.Add(new DeepSeekToolProposal(
                functionCall.CallId,
                functionCall.Name,
                (IDictionary<string, object?>?)functionCall.Arguments ?? new Dictionary<string, object?>()));
        }

        var text = response.Text;
        var usage = response.Usage;
        return new DeepSeekChatResult(
            correlationId,
            string.IsNullOrEmpty(text) ? null : text,
            proposals,
            response.FinishReason?.ToString(),
            new DeepSeekUsage(usage?.InputTokenCount, usage?.OutputTokenCount, usage?.TotalTokenCount));
    }

    /// <summary>
    /// Mapeia a exceção bruta para a classificação segura. Cancelamento do
    /// chamador vira <see cref="DeepSeekFailureKind.Cancelled"/>; cancelamento
    /// interno vira timeout.
    /// </summary>
    public static DeepSeekFailureKind ClassifyFailure(Exception exception, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException)
        {
            return callerToken.IsCancellationRequested
                ? DeepSeekFailureKind.Cancelled
                : DeepSeekFailureKind.Timeout;
        }

        if (exception is ClientResultException providerFailure)
        {
            return providerFailure.Status switch
            {
                408 => DeepSeekFailureKind.Timeout,
                429 => DeepSeekFailureKind.RateLimited,
                400 or 401 or 403 or 404 or 422 => DeepSeekFailureKind.ProviderInvalid,
                >= 500 => DeepSeekFailureKind.ProviderUnavailable,
                _ => DeepSeekFailureKind.ProviderUnavailable,
            };
        }

        if (exception is JsonException or InvalidOperationException)
        {
            return DeepSeekFailureKind.ProviderInvalid;
        }

        return DeepSeekFailureKind.ProviderUnavailable;
    }
}
