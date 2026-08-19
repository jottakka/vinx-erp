using System.Net;
using System.Text;

namespace Vinx.Erp.AI.UnitTests.Providers;

/// <summary>
/// Handler fake de fronteira compartilhado pelos testes de provider: captura
/// requests e responde sem rede, segredo ou endpoint real.
/// </summary>
internal sealed class TestProviderHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    internal TestProviderHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    internal int CallCount { get; private set; }

    internal List<string> RequestBodies { get; } = [];

    internal List<string> RequestPaths { get; } = [];

    internal static TestProviderHandler RespondsWith(string json, int statusCode = 200) => new(
        (request, _) => Task.FromResult(JsonResponse(json, statusCode)));

    internal static HttpResponseMessage JsonResponse(string json, int statusCode = 200) => new(
        (HttpStatusCode)statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    internal static string ChatCompletionJson(
        string content,
        string finishReason = "stop",
        string? toolCallId = null,
        string? toolCallName = null,
        string toolCallArguments = "{}",
        int promptTokens = 10,
        int completionTokens = 5,
        string extraTopLevelFields = "",
        string model = "test-model")
    {
        var toolCalls = toolCallId is null
            ? string.Empty
            : $@",""tool_calls"":[{{""id"":""{toolCallId}"",""type"":""function"",""function"":{{""name"":""{toolCallName}"",""arguments"":{Json(toolCallArguments)}}}}}]";
        return $$"""
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "created": 1755000000,
          "model": "{{model}}",
          "choices": [{
            "index": 0,
            "message": { "role": "assistant", "content": {{Json(content)}}{{toolCalls}} },
            "finish_reason": "{{finishReason}}"
          }],
          "usage": { "prompt_tokens": {{promptTokens}}, "completion_tokens": {{completionTokens}}, "total_tokens": {{promptTokens + completionTokens}} }{{extraTopLevelFields}}
        }
        """;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        RequestPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
        RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        return await _responder(request, cancellationToken);
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
