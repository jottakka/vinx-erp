using Microsoft.Extensions.AI;
using Vinx.Erp.AI.Providers.DeepSeek;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Providers.DeepSeek;

public sealed class DeepSeekRequestMapperTests
{
    private static DeepSeekOptions Approved(Action<DeepSeekOptions>? mutate = null)
    {
        var options = new DeepSeekOptions { ApiKey = Guid.NewGuid().ToString("N") };
        mutate?.Invoke(options);
        return options;
    }

    private static ChatMessage UserMessage(string text) => new(ChatRole.User, [new TextContent(text)]);

    [Fact]
    public void Empty_conversation_is_rejected()
    {
        Assert.Throws<DeepSeekRequestValidationException>(
            () => DeepSeekRequestMapper.Map(Approved(), [], null));
    }

    [Fact]
    public void Conversation_above_profile_message_limit_is_rejected()
    {
        var profile = Approved(o => o.MaxMessages = 2);
        var messages = new[] { UserMessage("a"), UserMessage("b"), UserMessage("c") };

        var exception = Assert.Throws<DeepSeekRequestValidationException>(
            () => DeepSeekRequestMapper.Map(profile, messages, null));

        Assert.Contains("messages", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Input_above_profile_character_limit_is_rejected()
    {
        var profile = Approved(o => o.MaxInputCharacters = 8);
        var messages = new[] { UserMessage("texto maior que o limite") };

        Assert.Throws<DeepSeekRequestValidationException>(
            () => DeepSeekRequestMapper.Map(profile, messages, null));
    }

    [Fact]
    public void Model_is_pinned_to_the_profile_and_request_model_is_ignored()
    {
        var profile = Approved();
        var requested = new ChatOptions { ModelId = "gpt-outro-provider" };

        var mapped = DeepSeekRequestMapper.Map(profile, [UserMessage("resuma")], requested);

        Assert.Equal(profile.Model, mapped.Options.ModelId);
    }

    [Fact]
    public void Requested_output_tokens_are_clamped_to_the_profile_limit()
    {
        var profile = Approved(o => o.MaxOutputTokens = 256);
        var requested = new ChatOptions { MaxOutputTokens = 999_999 };

        var mapped = DeepSeekRequestMapper.Map(profile, [UserMessage("resuma")], requested);

        Assert.Equal(256, mapped.Options.MaxOutputTokens);
    }

    [Fact]
    public void Missing_output_tokens_fall_back_to_the_profile_limit()
    {
        var profile = Approved(o => o.MaxOutputTokens = 128);

        var mapped = DeepSeekRequestMapper.Map(profile, [UserMessage("resuma")], null);

        Assert.Equal(128, mapped.Options.MaxOutputTokens);
    }

    [Fact]
    public void Tool_mode_is_pinned_to_auto_without_invocation_flag()
    {
        var requested = new ChatOptions { ToolMode = ChatToolMode.None };

        var mapped = DeepSeekRequestMapper.Map(Approved(), [UserMessage("leia")], requested);

        Assert.IsType<AutoChatToolMode>(mapped.Options.ToolMode);
    }

    [Fact]
    public void Trusted_context_metadata_is_stripped_from_messages()
    {
        var message = new ChatMessage(ChatRole.User, [new TextContent("resuma o documento")])
        {
            AuthorName = "ator-interno",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["jwt"] = "header.payload.signature",
                ["capability"] = "ai.read",
            },
        };

        var mapped = DeepSeekRequestMapper.Map(Approved(), [message], null);

        var sanitized = Assert.Single(mapped.Messages);
        Assert.Null(sanitized.AuthorName);
        Assert.Null(sanitized.AdditionalProperties);
    }

    [Fact]
    public void Request_options_additional_properties_are_dropped()
    {
        var requested = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["perfil"] = "interno" },
        };

        var mapped = DeepSeekRequestMapper.Map(Approved(), [UserMessage("resuma")], requested);

        Assert.Null(mapped.Options.AdditionalProperties);
    }

    [Fact]
    public void Image_content_is_rejected_because_the_hosted_profile_is_text_only()
    {
        // Contrato 2.2: a API hospedada é text-only; imagem nunca chega à wire.
        var message = new ChatMessage(
            ChatRole.User,
            [new TextContent("o que há na imagem?"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);

        Assert.Throws<DeepSeekRequestValidationException>(
            () => DeepSeekRequestMapper.Map(Approved(), [message], null));
    }

    [Fact]
    public void Unsupported_media_types_are_rejected_instead_of_dropped()
    {
        // Contrato 2.2: conteúdo de dados fora do perfil é rejeitado
        // deterministicamente, nunca descartado em silêncio.
        var message = new ChatMessage(
            ChatRole.User,
            [new TextContent("anexo"), new DataContent(new byte[] { 4, 5, 6 }, "application/pdf")]);

        Assert.Throws<DeepSeekRequestValidationException>(
            () => DeepSeekRequestMapper.Map(Approved(), [message], null));
    }

    [Fact]
    public void Unknown_roles_are_dropped()
    {
        var unknownRole = new ChatMessage(new ChatRole("developer"), [new TextContent("interno")]);

        var mapped = DeepSeekRequestMapper.Map(Approved(), [UserMessage("válido"), unknownRole], null);

        var sanitized = Assert.Single(mapped.Messages);
        Assert.Single(sanitized.Contents.OfType<TextContent>());
    }

    [Fact]
    public void Tool_protocol_contents_roundtrip()
    {
        var call = new FunctionCallContent("call-1", "leitura", new Dictionary<string, object?> { ["q"] = "pix" });
        var result = new FunctionResultContent("call-1", new Dictionary<string, object?> { ["total"] = 2 });
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant, [call]),
            new ChatMessage(ChatRole.Tool, [result]),
            UserMessage("continue"),
        };

        var mapped = DeepSeekRequestMapper.Map(Approved(), messages, null);

        Assert.Equal(3, mapped.Messages.Count);
        Assert.Contains(mapped.Messages[0].Contents, c => c is FunctionCallContent);
        Assert.Contains(mapped.Messages[1].Contents, c => c is FunctionResultContent);
    }

    [Fact]
    public void Messages_without_allowed_content_after_sanitization_are_rejected()
    {
        var onlyDisallowed = new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 7, 8, 9 }, "application/pdf")]);

        Assert.Throws<DeepSeekRequestValidationException>(
            () => DeepSeekRequestMapper.Map(Approved(), [onlyDisallowed], null));
    }

    [Fact]
    public void Null_arguments_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => DeepSeekRequestMapper.Map(Approved(), null!, null));
    }
}
