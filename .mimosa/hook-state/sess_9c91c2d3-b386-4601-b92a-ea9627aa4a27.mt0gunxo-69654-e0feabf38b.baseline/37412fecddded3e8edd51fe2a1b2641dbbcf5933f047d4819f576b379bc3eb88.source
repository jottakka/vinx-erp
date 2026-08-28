using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Vinx.Erp.AI.Providers.DeepSeek;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Providers.DeepSeek;

public sealed class DeepSeekChatClientContractTests
{
    private static DeepSeekOptions Approved(Action<DeepSeekOptions>? mutate = null)
    {
        var options = new DeepSeekOptions { ApiKey = Guid.NewGuid().ToString("N") };
        mutate?.Invoke(options);
        return options;
    }

    private static DeepSeekChatClient CreateClient(
        TestDeepSeekHandler handler,
        DeepSeekOptions? profile = null,
        ILogger? logger = null) =>
        new(profile ?? Approved(), new HttpClient(handler), logger);

    private static ChatMessage UserMessage(string text) => new(ChatRole.User, [new TextContent(text)]);

    private static JsonDocument RequestBody(TestDeepSeekHandler handler) =>
        JsonDocument.Parse(handler.RequestBodies[^1]);

    [Fact]
    public async Task Approved_profile_produces_a_typed_final_decision_with_correlation_id()
    {
        var handler = TestDeepSeekHandler.RespondsWith(
            TestDeepSeekHandler.ChatCompletionJson("resumo final"));
        using var client = CreateClient(handler);

        var response = await client.GetResponseAsync([UserMessage("resuma")]);

        Assert.Equal("resumo final", response.Text);
        var correlationId = Assert.IsType<string>(response.AdditionalProperties?["CorrelationId"]);
        Assert.NotEmpty(correlationId);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Request_cannot_choose_another_model_or_provider()
    {
        var handler = TestDeepSeekHandler.RespondsWith(
            TestDeepSeekHandler.ChatCompletionJson("ok"));
        var profile = Approved();
        using var client = CreateClient(handler, profile);

        await client.GetResponseAsync(
            [UserMessage("leia")],
            new ChatOptions { ModelId = "gpt-outro-provider", AdditionalProperties = new AdditionalPropertiesDictionary { ["provider"] = "openai" } });

        using var body = RequestBody(handler);
        Assert.Equal(profile.Model, body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("provider", out _));
    }

    [Fact]
    public async Task Runtime_mutation_of_the_options_object_is_rejected()
    {
        var handler = TestDeepSeekHandler.RespondsWith(
            TestDeepSeekHandler.ChatCompletionJson("ok"));
        var profile = Approved(o => o.MaxOutputTokens = 64);
        using var client = CreateClient(handler, profile);

        profile.Model = "modelo-trocado-em-runtime";
        profile.MaxOutputTokens = 999_999;
        await client.GetResponseAsync([UserMessage("leia")]);

        using var body = RequestBody(handler);
        Assert.Equal("deepseek-v4-flash", body.RootElement.GetProperty("model").GetString());
        var wireCap = body.RootElement.TryGetProperty("max_completion_tokens", out var completionTokens)
            ? completionTokens.GetInt32()
            : body.RootElement.GetProperty("max_tokens").GetInt32();
        Assert.Equal(64, wireCap);
    }

    [Fact]
    public async Task Output_token_cap_is_enforced_from_the_profile()
    {
        var handler = TestDeepSeekHandler.RespondsWith(
            TestDeepSeekHandler.ChatCompletionJson("ok"));
        var profile = Approved(o => o.MaxOutputTokens = 128);
        using var client = CreateClient(handler, profile);

        await client.GetResponseAsync([UserMessage("leia")], new ChatOptions { MaxOutputTokens = 999_999 });

        using var body = RequestBody(handler);
        var wireCap = body.RootElement.TryGetProperty("max_completion_tokens", out var completionTokens)
            ? completionTokens.GetInt32()
            : body.RootElement.GetProperty("max_tokens").GetInt32();
        Assert.Equal(128, wireCap);
    }

    [Fact]
    public async Task Allowed_multimodal_content_reaches_the_provider_as_image_url()
    {
        var handler = TestDeepSeekHandler.RespondsWith(
            TestDeepSeekHandler.ChatCompletionJson("descrição"));
        using var client = CreateClient(handler);
        var message = new ChatMessage(
            ChatRole.User,
            [new TextContent("o que é isto?"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);

        await client.GetResponseAsync([message]);

        var wire = handler.RequestBodies[^1];
        Assert.Contains("image_url", wire, StringComparison.Ordinal);
        Assert.Contains("data:image/png", wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trusted_context_metadata_never_reaches_the_wire()
    {
        var handler = TestDeepSeekHandler.RespondsWith(
            TestDeepSeekHandler.ChatCompletionJson("ok"));
        using var client = CreateClient(handler);
        var message = new ChatMessage(ChatRole.User, [new TextContent("resuma")])
        {
            AuthorName = "ator-interno",
            AdditionalProperties = new AdditionalPropertiesDictionary { ["jwt"] = "cabecalho.assinatura" },
        };

        await client.GetResponseAsync([message]);

        var wire = handler.RequestBodies[^1];
        Assert.DoesNotContain("ator-interno", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("jwt", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("cabecalho.assinatura", wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_call_response_becomes_a_proposal_and_the_adapter_never_invokes_tools()
    {
        var invoked = false;
        var tool = AIFunctionFactory.Create(() => { invoked = true; }, "leitura", "Lê dados autorizados");
        var handler = TestDeepSeekHandler.RespondsWith(
            TestDeepSeekHandler.ChatCompletionJson(
                string.Empty,
                finishReason: "tool_calls",
                toolCallId: "call-1",
                toolCallName: "leitura",
                toolCallArguments: "{\"q\":\"pix\"}"));
        using var client = CreateClient(handler);

        var response = await client.GetResponseAsync(
            [UserMessage("quantos pix?")],
            new ChatOptions { Tools = [tool], ToolMode = ChatToolMode.Auto });

        var proposal = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Single();
        Assert.Equal("leitura", proposal.Name);
        Assert.False(invoked);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Invalid_tool_arguments_do_not_invoke_tools_and_fail_as_provider_invalid()
    {
        var invoked = false;
        var tool = AIFunctionFactory.Create(() => { invoked = true; }, "leitura", "Lê dados autorizados");
        var handler = TestDeepSeekHandler.RespondsWith(
            TestDeepSeekHandler.ChatCompletionJson(
                string.Empty,
                finishReason: "tool_calls",
                toolCallId: "call-2",
                toolCallName: "leitura",
                toolCallArguments: "{ json invalido"));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(
            () => client.GetResponseAsync([UserMessage("quantos?")], new ChatOptions { Tools = [tool] }));

        Assert.Equal(DeepSeekFailureKind.ProviderInvalid, exception.Kind);
        Assert.False(invoked);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Unknown_response_fields_do_not_break_the_normalized_contract()
    {
        var handler = TestDeepSeekHandler.RespondsWith(
            TestDeepSeekHandler.ChatCompletionJson(
                "com campo novo",
                extraTopLevelFields: ",\"deepseek_novo_campo\":{\"x\":1}"));
        using var client = CreateClient(handler);

        var response = await client.GetResponseAsync([UserMessage("leia")]);

        Assert.Equal("com campo novo", response.Text);
    }

    [Fact]
    public async Task Timeout_is_classified_once_without_retry()
    {
        var profile = Approved(o => o.Timeout = TimeSpan.FromMilliseconds(150));
        var handler = new TestDeepSeekHandler(async (_, cancellation) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellation);
            return TestDeepSeekHandler.JsonResponse("nunca");
        });
        using var client = CreateClient(handler, profile);

        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(
            () => client.GetResponseAsync([UserMessage("lento")]));

        Assert.Equal(DeepSeekFailureKind.Timeout, exception.Kind);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_classified_as_cancelled_without_retry()
    {
        var handler = new TestDeepSeekHandler(async (_, cancellation) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellation);
            return TestDeepSeekHandler.JsonResponse("nunca");
        });
        using var client = CreateClient(handler, Approved(o => o.Timeout = TimeSpan.FromSeconds(30)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(
            () => client.GetResponseAsync([UserMessage("cancele")], options: null, cancellationToken: cancellation.Token));

        Assert.Equal(DeepSeekFailureKind.Cancelled, exception.Kind);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Rate_limited_and_unavailable_responses_are_classified_and_never_retried()
    {
        var rateLimited = TestDeepSeekHandler.RespondsWith(
            """{"error":{"message":"rate limit","type":"rate_limit_exceeded"}}""",
            statusCode: 429);
        using (var client = CreateClient(rateLimited))
        {
            var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(
                () => client.GetResponseAsync([UserMessage("a")]));
            Assert.Equal(DeepSeekFailureKind.RateLimited, exception.Kind);
            Assert.Equal(429, exception.HttpStatusCode);
            Assert.Equal(1, rateLimited.CallCount);
        }

        var unavailable = TestDeepSeekHandler.RespondsWith("{}", statusCode: 503);
        using (var client = CreateClient(unavailable))
        {
            var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(
                () => client.GetResponseAsync([UserMessage("b")]));
            Assert.Equal(DeepSeekFailureKind.ProviderUnavailable, exception.Kind);
            Assert.Equal(1, unavailable.CallCount);
        }

        var invalidCredentials = TestDeepSeekHandler.RespondsWith("{}", statusCode: 401);
        using (var client = CreateClient(invalidCredentials))
        {
            var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(
                () => client.GetResponseAsync([UserMessage("c")]));
            Assert.Equal(DeepSeekFailureKind.ProviderInvalid, exception.Kind);
            Assert.Equal(401, exception.HttpStatusCode);
            Assert.Equal(1, invalidCredentials.CallCount);
        }
    }

    [Fact]
    public async Task Failure_messages_are_redacted()
    {
        var secretBody = " corpo-secreto-com-dados-do-cliente ";
        var errorBody = "{\"error\":{\"message\":\"" + secretBody + "\",\"type\":\"server_error\"}}";
        var handler = TestDeepSeekHandler.RespondsWith(errorBody, statusCode: 500);
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(
            () => client.GetResponseAsync([UserMessage("segredo")]));

        Assert.DoesNotContain("corpo-secreto", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("segredo", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error_logs_never_contain_body_prompt_or_key()
    {
        var logger = new RecordingLogger();
        var apiKey = Guid.NewGuid().ToString("N");
        var profile = Approved(o => o.ApiKey = apiKey);
        var handler = TestDeepSeekHandler.RespondsWith(
            """{"error":{"message":"conteudo interno do provider"}}""",
            statusCode: 500);
        using var client = CreateClient(handler, profile, logger);

        await Assert.ThrowsAsync<DeepSeekProviderException>(
            () => client.GetResponseAsync([UserMessage("prompt-interno-nao-loga")]));

        var logs = logger.Text;
        Assert.Contains("provider_unavailable", logs, StringComparison.Ordinal);
        Assert.Contains("ai/deepseek", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("conteudo interno", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt-interno", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Input_above_profile_limits_fails_before_any_provider_call()
    {
        var handler = TestDeepSeekHandler.RespondsWith(
            TestDeepSeekHandler.ChatCompletionJson("ok"));
        var profile = Approved(o => o.MaxMessages = 1);
        using var client = CreateClient(handler, profile);

        await Assert.ThrowsAsync<DeepSeekRequestValidationException>(
            () => client.GetResponseAsync([UserMessage("a"), UserMessage("b")]));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Parallel_runs_isolate_responses_budget_and_correlation()
    {
        var handler = new TestDeepSeekHandler((request, _) =>
        {
            var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement;
            var messages = body.GetProperty("messages");
            var prompt = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString();
            return Task.FromResult(TestDeepSeekHandler.JsonResponse(
                TestDeepSeekHandler.ChatCompletionJson($"eco:{prompt}")));
        });
        using var client = CreateClient(handler);

        var responses = await Task.WhenAll(
            client.GetResponseAsync([UserMessage("alfa")]),
            client.GetResponseAsync([UserMessage("beta")]));

        var ecos = responses.Select(response => response.Text).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["eco:alfa", "eco:beta"], ecos);
        var correlations = responses
            .Select(response => Assert.IsType<string>(response.AdditionalProperties?["CorrelationId"]))
            .ToArray();
        Assert.Equal(2, correlations.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Usage_is_reported_for_budget_accounting()
    {
        var handler = TestDeepSeekHandler.RespondsWith(
            TestDeepSeekHandler.ChatCompletionJson("ok", promptTokens: 40, completionTokens: 8));
        using var client = CreateClient(handler);

        var response = await client.GetResponseAsync([UserMessage("leia")]);

        Assert.Equal(40, response.Usage?.InputTokenCount);
        Assert.Equal(8, response.Usage?.OutputTokenCount);
        Assert.Equal(48, response.Usage?.TotalTokenCount);
    }

    [Fact]
    public void Streaming_is_outside_the_approved_profile()
    {
        using var client = CreateClient(TestDeepSeekHandler.RespondsWith("{}"));

        Assert.Throws<NotSupportedException>(
            () => client.GetStreamingResponseAsync([UserMessage("leia")]));
    }

    [Fact]
    public void Constructor_rejects_null_profile_and_null_http_client()
    {
        Assert.Throws<ArgumentNullException>(() => new DeepSeekChatClient(null!, new HttpClient()));
        Assert.Throws<ArgumentNullException>(
            () => new DeepSeekChatClient(Approved(), null!));
    }
}
