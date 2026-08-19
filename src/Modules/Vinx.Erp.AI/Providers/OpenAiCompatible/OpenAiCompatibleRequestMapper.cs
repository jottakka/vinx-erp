using Microsoft.Extensions.AI;

namespace Vinx.Erp.AI.Providers.OpenAiCompatible;

/// <summary>
/// Rejeição determinística do request antes de qualquer chamada HTTP: o
/// conteúdo viola os limites ou as modalidades do perfil aprovado. Mensagem
/// fixa, sem conteúdo do request.
/// </summary>
public sealed class OpenAiCompatibleRequestValidationException(string message) : Exception(message);

/// <summary>Request normalizado e sanitizado pronto para o protocolo aprovado.</summary>
public sealed record OpenAiCompatibleMappedRequest(
    IReadOnlyList<ChatMessage> Messages,
    ChatOptions Options,
    long InputCharacters,
    int ImageCount,
    long ImageBytes);

/// <summary>
/// Converte o request normalizado interno no formato aprovado: fixa modelo e
/// limites do perfil, mantém somente conteúdo permitido pelas modalidades
/// declaradas e remove qualquer metadado de contexto confiável (author,
/// additional properties) antes da fronteira do provider. Desde o contrato
/// 2.2, conteúdo de dados fora do perfil é rejeitado deterministicamente —
/// nunca descartado em silêncio.
/// </summary>
public static class OpenAiCompatibleRequestMapper
{
    private static readonly HashSet<string> AllowedImageMediaTypes =
        new(StringComparer.Ordinal) { "image/png", "image/jpeg", "image/webp" };

    private static readonly HashSet<ChatRole> AllowedRoles =
        [ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool];

    public static OpenAiCompatibleMappedRequest Map(
        OpenAiCompatibleOptions profile,
        IEnumerable<ChatMessage>? messages,
        ChatOptions? requested)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(messages);

        var source = messages.ToList();
        if (source.Count == 0)
        {
            throw new OpenAiCompatibleRequestValidationException("conversation is empty");
        }

        if (source.Count > profile.MaxMessages)
        {
            throw new OpenAiCompatibleRequestValidationException(
                $"conversation exceeds the approved profile limit of {profile.MaxMessages} messages");
        }

        var sanitized = new List<ChatMessage>(source.Count);
        long inputCharacters = 0;
        int imageCount = 0;
        long imageBytes = 0;
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
                    case DataContent data:
                        ValidateImageData(profile, data);
                        imageCount++;
                        if (imageCount > profile.MaxImagesPerRequest)
                        {
                            throw new OpenAiCompatibleRequestValidationException(
                                "conversation exceeds the approved image count limit");
                        }

                        imageBytes += data.Data.Length;
                        if (imageBytes > profile.MaxImageBytesPerRequest)
                        {
                            throw new OpenAiCompatibleRequestValidationException(
                                "conversation exceeds the approved aggregate image byte limit");
                        }

                        contents.Add(new DataContent(data.Data, data.MediaType));
                        break;
                    case FunctionCallContent or FunctionResultContent:
                        contents.Add(content);
                        break;
                    default:
                        // Demais tipos de conteúdo são bookkeeping de transporte,
                        // não conteúdo do usuário: descartados antes da fronteira.
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
            throw new OpenAiCompatibleRequestValidationException("conversation has no allowed content");
        }

        if (inputCharacters > profile.MaxInputCharacters)
        {
            throw new OpenAiCompatibleRequestValidationException(
                $"input exceeds the approved profile limit of {profile.MaxInputCharacters} characters");
        }

        // Instructions viajam na wire e contam no teto determinístico de entrada.
        inputCharacters += requested?.Instructions?.Length ?? 0;
        if (inputCharacters > profile.MaxInputCharacters)
        {
            throw new OpenAiCompatibleRequestValidationException(
                $"input exceeds the approved profile limit of {profile.MaxInputCharacters} characters");
        }

        // Orçamento de admissão preflight por imagem; usage real é reportado
        // após a execução.
        var estimatedImageTokens = (long)imageCount * profile.EstimatedTokensPerImage;
        if (estimatedImageTokens > profile.MaxTotalTokensPerCall - profile.MaxOutputTokens)
        {
            throw new OpenAiCompatibleRequestValidationException(
                "image admission estimate exceeds the approved per-call budget");
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

        return new OpenAiCompatibleMappedRequest(sanitized, options, inputCharacters, imageCount, imageBytes);
    }

    private static void ValidateImageData(OpenAiCompatibleOptions profile, DataContent data)
    {
        if (!profile.AllowImageInputs)
        {
            throw new OpenAiCompatibleRequestValidationException(
                "conversation contains image content but the approved profile is text-only");
        }

        if (data.MediaType is null || !AllowedImageMediaTypes.Contains(data.MediaType))
        {
            throw new OpenAiCompatibleRequestValidationException(
                "conversation contains a content media type outside the approved list");
        }

        if (data.Uri is null || !data.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // O servidor nunca busca URL de request: somente dados embutidos.
            // O ME.AI já exige data URI no construtor; isto é defesa de fronteira.
            throw new OpenAiCompatibleRequestValidationException(
                "image content by URL is not allowed; only image bytes are accepted");
        }

        if (data.Data.Length == 0)
        {
            throw new OpenAiCompatibleRequestValidationException("image content is empty");
        }

        if (data.Data.Length > profile.MaxImageBytesPerImage)
        {
            throw new OpenAiCompatibleRequestValidationException(
                "image exceeds the approved per-image byte limit");
        }
    }
}
