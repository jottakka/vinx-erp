using Microsoft.Extensions.AI;
using Vinx.Erp.AI.Providers.OpenAiCompatible;

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
/// Falha classificada do provider. A mensagem é fixa e redacted e a cadeia
/// pública não carrega causa bruta: somente kind e status HTTP.
/// </summary>
public sealed class DeepSeekProviderException(DeepSeekFailureKind kind, int? httpStatusCode = null) : Exception(FormatMessage(kind, httpStatusCode))
{
    public DeepSeekFailureKind Kind { get; } = kind;

    public int? HttpStatusCode { get; } = httpStatusCode;

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
/// Fachada do perfil DeepSeek sobre o mapper do núcleo OpenAI-compatible:
/// converte a resposta do provider no contrato normalizado e classifica
/// falhas externas. Campos desconhecidos são ignorados; schema inválido de
/// tool vira falha provider_invalid, nunca resultado inventado.
/// </summary>
public static class DeepSeekResponseMapper
{
    public static DeepSeekChatResult ToResult(ChatResponse? response, string correlationId)
    {
        try
        {
            var result = OpenAiCompatibleResponseMapper.ToResult(response, correlationId);
            return new DeepSeekChatResult(
                result.CorrelationId,
                result.FinalText,
                [.. result.ToolProposals
                    .Select(proposal => new DeepSeekToolProposal(
                        proposal.CallId, proposal.Name, proposal.Arguments))],
                result.FinishReason,
                new DeepSeekUsage(result.Usage.InputTokens, result.Usage.OutputTokens, result.Usage.TotalTokens));
        }
        catch (OpenAiCompatibleProviderException exception)
        {
            // Sem inner: a cadeia pública nunca carrega a causa bruta do provider.
            throw new DeepSeekProviderException(
                DeepSeekChatClient.Translate(exception.Kind), exception.HttpStatusCode);
        }
    }

    /// <summary>
    /// Mapeia a exceção bruta para a classificação segura. Cancelamento do
    /// chamador vira <see cref="DeepSeekFailureKind.Cancelled"/>; cancelamento
    /// interno vira timeout.
    /// </summary>
    public static DeepSeekFailureKind ClassifyFailure(Exception exception, CancellationToken callerToken) =>
        DeepSeekChatClient.Translate(
            OpenAiCompatibleResponseMapper.ClassifyFailure(exception, callerToken));
}
