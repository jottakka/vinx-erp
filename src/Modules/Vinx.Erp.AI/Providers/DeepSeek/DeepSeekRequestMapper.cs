using Microsoft.Extensions.AI;
using Vinx.Erp.AI.Providers.OpenAiCompatible;

namespace Vinx.Erp.AI.Providers.DeepSeek;

/// <summary>
/// Rejeição determinística do request antes de qualquer chamada HTTP: o
/// conteúdo viola os limites do perfil aprovado. Mensagem fixa, sem conteúdo
/// do request.
/// </summary>
public sealed class DeepSeekRequestValidationException(string message) : Exception(message);

/// <summary>Request normalizado e sanitizado pronto para o protocolo aprovado.</summary>
public sealed record DeepSeekMappedRequest(
    IReadOnlyList<ChatMessage> Messages,
    ChatOptions Options,
    long InputCharacters);

/// <summary>
/// Fachada do perfil DeepSeek sobre o mapper do núcleo OpenAI-compatible:
/// fixa modelo e limites do perfil, mantém somente conteúdo permitido e
/// remove qualquer metadado de contexto confiável (author, additional
/// properties) antes da fronteira do provider. Desde o contrato 2.2 a API
/// hospedada é text-only: imagem é rejeitada antes de HTTP.
/// </summary>
public static class DeepSeekRequestMapper
{
    public static DeepSeekMappedRequest Map(
        DeepSeekOptions profile,
        IEnumerable<ChatMessage>? messages,
        ChatOptions? requested)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(messages);

        try
        {
            var mapped = OpenAiCompatibleRequestMapper.Map(
                profile.ToOpenAiCompatibleProfile(), messages, requested);
            return new DeepSeekMappedRequest(mapped.Messages, mapped.Options, mapped.InputCharacters);
        }
        catch (OpenAiCompatibleRequestValidationException exception)
        {
            throw new DeepSeekRequestValidationException(exception.Message);
        }
    }
}
