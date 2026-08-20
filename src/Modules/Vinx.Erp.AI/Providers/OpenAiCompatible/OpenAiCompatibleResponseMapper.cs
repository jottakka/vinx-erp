using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Vinx.Erp.AI.Providers.OpenAiCompatible;

/// <summary>Classificação segura da falha externa; nunca vira sucesso inventado.</summary>
public enum OpenAiCompatibleFailureKind
{
    Timeout,
    Cancelled,
    RateLimited,
    ProviderInvalid,
    ProviderUnavailable,
}

/// <summary>
/// Falha classificada do provider. A mensagem é fixa e redacted e a cadeia
/// pública não carrega causa bruta (Exception.ToString() percorreria o body
/// do provider): somente kind, status HTTP e correlation seguro.
/// </summary>
public sealed class OpenAiCompatibleProviderException(OpenAiCompatibleFailureKind kind, int? httpStatusCode = null) : Exception(FormatMessage(kind, httpStatusCode))
{
    public OpenAiCompatibleFailureKind Kind { get; } = kind;

    public int? HttpStatusCode { get; } = httpStatusCode;

    private static string FormatMessage(OpenAiCompatibleFailureKind kind, int? httpStatusCode) =>
        httpStatusCode is int status
            ? $"OpenAI-compatible provider call failed: {kind.ToString().ToLowerInvariant()} ({status})"
            : $"OpenAI-compatible provider call failed: {kind.ToString().ToLowerInvariant()}";
}

/// <summary>Consumo de tokens reportado para o orçamento do runtime.</summary>
public sealed record OpenAiCompatibleUsage(long? InputTokens, long? OutputTokens, long? TotalTokens);

/// <summary>Proposta de leitura tipada; o adapter nunca a executa.</summary>
public sealed record OpenAiCompatibleToolProposal(
    string CallId,
    string Name,
    IDictionary<string, object?> Arguments);

/// <summary>
/// Resposta normalizada: decisão final tipada ou proposta de tool, com
/// correlation ID da execução e consumo para o orçamento.
/// </summary>
public sealed record OpenAiCompatibleChatResult(
    string CorrelationId,
    string? FinalText,
    IReadOnlyList<OpenAiCompatibleToolProposal> ToolProposals,
    string? FinishReason,
    OpenAiCompatibleUsage Usage)
{
    public bool HasToolProposals => ToolProposals.Count > 0;
}

/// <summary>
/// Converte a resposta do provider no contrato normalizado e classifica falhas
/// externas. Campos desconhecidos são ignorados; schema inválido de tool vira
/// falha provider_invalid, nunca resultado inventado.
/// </summary>
public static class OpenAiCompatibleResponseMapper
{
    public static OpenAiCompatibleChatResult ToResult(ChatResponse? response, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var proposals = new List<OpenAiCompatibleToolProposal>();
        foreach (var functionCall in response.Messages.SelectMany(message => message.Contents)
                     .OfType<FunctionCallContent>())
        {
            if (functionCall.Exception is not null)
            {
                // Sem inner: a exceção de parse pode ecoar argumentos brutos.
                throw new OpenAiCompatibleProviderException(
                    OpenAiCompatibleFailureKind.ProviderInvalid);
            }

            proposals.Add(new OpenAiCompatibleToolProposal(
                functionCall.CallId,
                functionCall.Name,
                (IDictionary<string, object?>?)functionCall.Arguments ?? new Dictionary<string, object?>()));
        }

        var text = response.Text;
        var usage = response.Usage;
        return new OpenAiCompatibleChatResult(
            correlationId,
            string.IsNullOrEmpty(text) ? null : text,
            proposals,
            response.FinishReason?.ToString(),
            new OpenAiCompatibleUsage(usage?.InputTokenCount, usage?.OutputTokenCount, usage?.TotalTokenCount));
    }

    /// <summary>
    /// Mapeia a exceção bruta para a classificação segura. Cancelamento do
    /// chamador vira <see cref="OpenAiCompatibleFailureKind.Cancelled"/>;
    /// cancelamento interno vira timeout.
    /// </summary>
    public static OpenAiCompatibleFailureKind ClassifyFailure(Exception exception, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException)
        {
            return callerToken.IsCancellationRequested
                ? OpenAiCompatibleFailureKind.Cancelled
                : OpenAiCompatibleFailureKind.Timeout;
        }

        if (exception is ClientResultException providerFailure)
        {
            return providerFailure.Status switch
            {
                408 => OpenAiCompatibleFailureKind.Timeout,
                429 => OpenAiCompatibleFailureKind.RateLimited,
                400 or 401 or 403 or 404 or 422 => OpenAiCompatibleFailureKind.ProviderInvalid,
                >= 500 => OpenAiCompatibleFailureKind.ProviderUnavailable,
                _ => OpenAiCompatibleFailureKind.ProviderUnavailable,
            };
        }

        if (exception is JsonException or InvalidOperationException)
        {
            return OpenAiCompatibleFailureKind.ProviderInvalid;
        }

        return OpenAiCompatibleFailureKind.ProviderUnavailable;
    }
}
