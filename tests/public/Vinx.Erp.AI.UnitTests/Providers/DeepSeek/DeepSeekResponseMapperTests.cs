using System.Text.Json;
using Microsoft.Extensions.AI;
using Vinx.Erp.AI.Providers.DeepSeek;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Providers.DeepSeek;

public sealed class DeepSeekResponseMapperTests
{
    [Fact]
    public void Final_text_response_maps_to_a_typed_decision_with_correlation()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, [new TextContent("resumo aprovado")]))
        {
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5, TotalTokenCount = 15 },
        };

        var result = DeepSeekResponseMapper.ToResult(response, "corr-1");

        Assert.Equal("corr-1", result.CorrelationId);
        Assert.Equal("resumo aprovado", result.FinalText);
        Assert.Empty(result.ToolProposals);
        Assert.False(result.HasToolProposals);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal(10, result.Usage.InputTokens);
        Assert.Equal(5, result.Usage.OutputTokens);
        Assert.Equal(15, result.Usage.TotalTokens);
    }

    [Fact]
    public void Tool_call_maps_to_a_typed_proposal_with_parsed_arguments()
    {
        var arguments = new Dictionary<string, object?> { ["query"] = "pix" };
        var response = new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call-9", "leitura", arguments)]));

        var result = DeepSeekResponseMapper.ToResult(response, "corr-2");

        var proposal = Assert.Single(result.ToolProposals);
        Assert.Equal("call-9", proposal.CallId);
        Assert.Equal("leitura", proposal.Name);
        Assert.Equal("pix", proposal.Arguments["query"] as string);
        Assert.Null(result.FinalText);
        Assert.True(result.HasToolProposals);
    }

    [Fact]
    public void Invalid_tool_arguments_are_rejected_as_provider_invalid()
    {
        var invalid = FunctionCallContent.CreateFromParsedArguments(
            "{ not json",
            "call-10",
            "leitura",
            static json => throw new JsonException("bad"));
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, [invalid]));

        var exception = Assert.Throws<DeepSeekProviderException>(
            () => DeepSeekResponseMapper.ToResult(response, "corr-3"));

        Assert.Equal(DeepSeekFailureKind.ProviderInvalid, exception.Kind);
    }

    [Fact]
    public void Function_call_content_with_exception_is_rejected_as_provider_invalid()
    {
        var failed = new FunctionCallContent("leitura", "call-11", new Dictionary<string, object?>())
        {
            Exception = new InvalidOperationException("schema"),
        };
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, [failed]));

        var exception = Assert.Throws<DeepSeekProviderException>(
            () => DeepSeekResponseMapper.ToResult(response, "corr-4"));

        Assert.Equal(DeepSeekFailureKind.ProviderInvalid, exception.Kind);
    }

    [Fact]
    public void Missing_usage_maps_to_null_token_counts()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, [new TextContent("ok")]));

        var result = DeepSeekResponseMapper.ToResult(response, "corr-5");

        Assert.Null(result.Usage.InputTokens);
        Assert.Null(result.Usage.TotalTokens);
    }

    [Fact]
    public void Null_response_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => DeepSeekResponseMapper.ToResult(null!, "corr-6"));
    }

    [Fact]
    public void Caller_cancellation_is_classified_as_cancelled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var kind = DeepSeekResponseMapper.ClassifyFailure(
            new OperationCanceledException(source.Token),
            source.Token);

        Assert.Equal(DeepSeekFailureKind.Cancelled, kind);
    }

    [Fact]
    public void Internal_timeout_is_classified_as_timeout()
    {
        using var uncancelled = new CancellationTokenSource();

        var kind = DeepSeekResponseMapper.ClassifyFailure(
            new OperationCanceledException(),
            uncancelled.Token);

        Assert.Equal(DeepSeekFailureKind.Timeout, kind);
    }

    [Fact]
    public void Transport_and_serialization_failures_map_to_safe_kinds()
    {
        using var uncancelled = new CancellationTokenSource();

        Assert.Equal(
            DeepSeekFailureKind.ProviderUnavailable,
            DeepSeekResponseMapper.ClassifyFailure(new HttpRequestException("dns"), uncancelled.Token));
        Assert.Equal(
            DeepSeekFailureKind.ProviderInvalid,
            DeepSeekResponseMapper.ClassifyFailure(new JsonException("parse"), uncancelled.Token));
        Assert.Equal(
            DeepSeekFailureKind.ProviderInvalid,
            DeepSeekResponseMapper.ClassifyFailure(new InvalidOperationException("inesperado"), uncancelled.Token));
        Assert.Equal(
            DeepSeekFailureKind.ProviderUnavailable,
            DeepSeekResponseMapper.ClassifyFailure(new FormatException("desconhecido"), uncancelled.Token));
    }
}
