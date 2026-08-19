using Microsoft.Extensions.AI;
using Vinx.Erp.AI.Providers.OpenAiCompatible;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Providers.OpenAiCompatible;

public sealed class OpenAiCompatibleRequestMapperTests
{
    private static OpenAiCompatibleOptions Approved(Action<OpenAiCompatibleOptions>? mutate = null)
    {
        var options = new OpenAiCompatibleOptions
        {
            Endpoint = new Uri("https://unit-test.invalid/v1/", UriKind.Absolute),
            Model = "test-model",
            ApiKey = Guid.NewGuid().ToString("N"),
        };
        mutate?.Invoke(options);
        return options;
    }

    private static OpenAiCompatibleOptions Vision(Action<OpenAiCompatibleOptions>? mutate = null) =>
        Approved(o =>
        {
            o.AllowImageInputs = true;
            o.MaxImagesPerRequest = 2;
            o.MaxImageBytesPerImage = 8;
            o.MaxImageBytesPerRequest = 12;
            o.EstimatedTokensPerImage = 1_000;
            mutate?.Invoke(o);
        });

    private static ChatMessage UserMessage(string text) => new(ChatRole.User, [new TextContent(text)]);

    private static ChatMessage ImageMessage(params byte[][] images) => new(
        ChatRole.User,
        [new TextContent("o que há na imagem?"), .. images.Select(image => new DataContent(image, "image/png"))]);

    [Fact]
    public void Empty_conversation_is_rejected()
    {
        Assert.Throws<OpenAiCompatibleRequestValidationException>(
            () => OpenAiCompatibleRequestMapper.Map(Approved(), [], null));
    }

    [Fact]
    public void Conversation_above_profile_message_limit_is_rejected()
    {
        var profile = Approved(o => o.MaxMessages = 2);
        var messages = new[] { UserMessage("a"), UserMessage("b"), UserMessage("c") };

        var exception = Assert.Throws<OpenAiCompatibleRequestValidationException>(
            () => OpenAiCompatibleRequestMapper.Map(profile, messages, null));

        Assert.Contains("messages", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Input_above_profile_character_limit_is_rejected()
    {
        var profile = Approved(o => o.MaxInputCharacters = 8);
        var messages = new[] { UserMessage("texto maior que o limite") };

        Assert.Throws<OpenAiCompatibleRequestValidationException>(
            () => OpenAiCompatibleRequestMapper.Map(profile, messages, null));
    }

    [Fact]
    public void Model_is_pinned_to_the_profile_and_request_model_is_ignored()
    {
        var profile = Approved();
        var requested = new ChatOptions { ModelId = "modelo-do-request" };

        var mapped = OpenAiCompatibleRequestMapper.Map(profile, [UserMessage("resuma")], requested);

        Assert.Equal(profile.Model, mapped.Options.ModelId);
    }

    [Fact]
    public void Requested_output_tokens_are_clamped_to_the_profile_limit()
    {
        var profile = Approved(o => o.MaxOutputTokens = 256);
        var requested = new ChatOptions { MaxOutputTokens = 999_999 };

        var mapped = OpenAiCompatibleRequestMapper.Map(profile, [UserMessage("resuma")], requested);

        Assert.Equal(256, mapped.Options.MaxOutputTokens);
    }

    [Fact]
    public void Missing_output_tokens_fall_back_to_the_profile_limit()
    {
        var profile = Approved(o => o.MaxOutputTokens = 128);

        var mapped = OpenAiCompatibleRequestMapper.Map(profile, [UserMessage("resuma")], null);

        Assert.Equal(128, mapped.Options.MaxOutputTokens);
    }

    [Fact]
    public void Tool_mode_is_pinned_to_auto_without_invocation_flag()
    {
        var requested = new ChatOptions { ToolMode = ChatToolMode.None };

        var mapped = OpenAiCompatibleRequestMapper.Map(Approved(), [UserMessage("leia")], requested);

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

        var mapped = OpenAiCompatibleRequestMapper.Map(Approved(), [message], null);

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

        var mapped = OpenAiCompatibleRequestMapper.Map(Approved(), [UserMessage("resuma")], requested);

        Assert.Null(mapped.Options.AdditionalProperties);
    }

    [Fact]
    public void Image_bytes_within_the_caps_are_admitted_and_counted()
    {
        var mapped = OpenAiCompatibleRequestMapper.Map(
            Vision(), [ImageMessage(new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 })], null);

        var contents = Assert.Single(mapped.Messages).Contents;
        Assert.Equal(2, contents.OfType<DataContent>().Count());
        Assert.Equal(2, mapped.ImageCount);
        Assert.Equal(6, mapped.ImageBytes);
    }

    [Fact]
    public void Image_on_a_text_only_profile_is_rejected_before_http()
    {
        Assert.Throws<OpenAiCompatibleRequestValidationException>(
            () => OpenAiCompatibleRequestMapper.Map(Approved(), [ImageMessage(new byte[] { 1, 2, 3 })], null));
    }

    [Fact]
    public void Image_wrapped_in_a_data_uri_decodes_to_bytes_and_is_admitted()
    {
        // ME.AI só representa dados embutidos: o ctor exige data URI, e o
        // mapper ainda rejeita esquema não-data como defesa de fronteira.
        var message = new ChatMessage(
            ChatRole.User,
            [new TextContent("descreva"), new DataContent(new Uri("data:image/png;base64,AQID"), "image/png")]);

        var mapped = OpenAiCompatibleRequestMapper.Map(Vision(), [message], null);

        Assert.Equal(1, mapped.ImageCount);
        Assert.Equal(3, mapped.ImageBytes);
    }

    [Fact]
    public void Unsupported_media_type_is_rejected_on_a_vision_profile()
    {
        var message = new ChatMessage(
            ChatRole.User,
            [new TextContent("anexo"), new DataContent(new byte[] { 4, 5, 6 }, "application/pdf")]);

        Assert.Throws<OpenAiCompatibleRequestValidationException>(
            () => OpenAiCompatibleRequestMapper.Map(Vision(), [message], null));
    }

    [Fact]
    public void Image_count_above_the_profile_limit_is_rejected()
    {
        var profile = Vision(o => o.MaxImagesPerRequest = 1);

        Assert.Throws<OpenAiCompatibleRequestValidationException>(
            () => OpenAiCompatibleRequestMapper.Map(profile, [ImageMessage(new byte[] { 1 }, new byte[] { 2 })], null));
    }

    [Fact]
    public void Image_above_the_per_image_byte_limit_is_rejected()
    {
        var profile = Vision(o => o.MaxImageBytesPerImage = 2);

        Assert.Throws<OpenAiCompatibleRequestValidationException>(
            () => OpenAiCompatibleRequestMapper.Map(profile, [ImageMessage(new byte[] { 1, 2, 3 })], null));
    }

    [Fact]
    public void Images_above_the_aggregate_byte_limit_are_rejected()
    {
        // Cada imagem dentro do teto individual, a soma acima do agregado.
        var profile = Vision(o => o.MaxImageBytesPerRequest = 10);

        Assert.Throws<OpenAiCompatibleRequestValidationException>(
            () => OpenAiCompatibleRequestMapper.Map(profile, [ImageMessage(new byte[] { 1, 2, 3, 4, 5, 6 }, new byte[] { 7, 8, 9, 10, 11 })], null));
    }

    [Fact]
    public void Image_admission_estimate_above_the_call_budget_is_rejected()
    {
        var profile = Vision(o => o.EstimatedTokensPerImage = 100_000);

        Assert.Throws<OpenAiCompatibleRequestValidationException>(
            () => OpenAiCompatibleRequestMapper.Map(profile, [ImageMessage(new byte[] { 1 })], null));
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

        var mapped = OpenAiCompatibleRequestMapper.Map(Approved(), messages, null);

        Assert.Equal(3, mapped.Messages.Count);
        Assert.Contains(mapped.Messages[0].Contents, c => c is FunctionCallContent);
        Assert.Contains(mapped.Messages[1].Contents, c => c is FunctionResultContent);
    }

    [Fact]
    public void Messages_without_allowed_content_after_sanitization_are_rejected()
    {
        var onlyUnknownRole = new ChatMessage(new ChatRole("developer"), [new TextContent("interno")]);

        Assert.Throws<OpenAiCompatibleRequestValidationException>(
            () => OpenAiCompatibleRequestMapper.Map(Approved(), [onlyUnknownRole], null));
    }

    [Fact]
    public void Instructions_count_against_the_character_limit()
    {
        var profile = Approved(o => o.MaxInputCharacters = 8);
        var requested = new ChatOptions { Instructions = new string('i', 32) };

        Assert.Throws<OpenAiCompatibleRequestValidationException>(
            () => OpenAiCompatibleRequestMapper.Map(profile, [UserMessage("curto")], requested));
    }

    [Fact]
    public void Null_arguments_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => OpenAiCompatibleRequestMapper.Map(Approved(), null!, null));
    }
}
