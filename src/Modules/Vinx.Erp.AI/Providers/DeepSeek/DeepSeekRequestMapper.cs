using Microsoft.Extensions.AI;

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
/// Converte o request normalizado interno no formato aprovado: fixa modelo e
/// limites do perfil, mantém somente conteúdo permitido e remove qualquer
/// metadado de contexto confiável (author, additional properties) antes da
/// fronteira do provider.
/// </summary>
public static class DeepSeekRequestMapper
{
    private static readonly HashSet<string> AllowedImageMediaTypes =
        new(StringComparer.Ordinal) { "image/png", "image/jpeg", "image/webp" };

    private static readonly HashSet<ChatRole> AllowedRoles =
        [ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool];

    public static DeepSeekMappedRequest Map(
        DeepSeekOptions profile,
        IEnumerable<ChatMessage>? messages,
        ChatOptions? requested)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(messages);

        var source = messages.ToList();
        if (source.Count == 0)
        {
            throw new DeepSeekRequestValidationException("conversation is empty");
        }

        if (source.Count > profile.MaxMessages)
        {
            throw new DeepSeekRequestValidationException(
                $"conversation exceeds the approved profile limit of {profile.MaxMessages} messages");
        }

        var sanitized = new List<ChatMessage>(source.Count);
        long inputCharacters = 0;
        foreach (var message in source)
        {
            if (!AllowedRoles.Contains(message.Role))
            {
                continue;
            }

            var contents = new List<AIContent>();
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        contents.Add(new TextContent(text.Text));
                        inputCharacters += text.Text?.Length ?? 0;
                        break;
                    case DataContent data when data.MediaType is not null
                        && AllowedImageMediaTypes.Contains(data.MediaType):
                        contents.Add(new DataContent(data.Data, data.MediaType));
                        break;
                    case FunctionCallContent or FunctionResultContent:
                        contents.Add(content);
                        break;
                    default:
                        // Conteúdo não permitido é descartado antes da fronteira.
                        break;
                }
            }

            if (contents.Count == 0)
            {
                continue;
            }

            sanitized.Add(new ChatMessage(message.Role, contents));
        }

        if (sanitized.Count == 0)
        {
            throw new DeepSeekRequestValidationException("conversation has no allowed content");
        }

        if (inputCharacters > profile.MaxInputCharacters)
        {
            throw new DeepSeekRequestValidationException(
                $"input exceeds the approved profile limit of {profile.MaxInputCharacters} characters");
        }

        var options = new ChatOptions
        {
            // Pinned pelo perfil: o request nunca escolhe modelo, limite ou provider.
            ModelId = profile.Model,
            Instructions = requested?.Instructions,
            Temperature = requested?.Temperature,
            TopP = requested?.TopP,
            MaxOutputTokens = Math.Min(
                requested?.MaxOutputTokens ?? profile.MaxOutputTokens,
                profile.MaxOutputTokens),
            Tools = requested?.Tools,
            // Auto permite o modelo propor tools; a invocação é impossível porque o
            // adapter não monta pipeline de invocação (FunctionInvokingChatClient).
            ToolMode = ChatToolMode.Auto,
        };

        return new DeepSeekMappedRequest(sanitized, options, inputCharacters);
    }
}
