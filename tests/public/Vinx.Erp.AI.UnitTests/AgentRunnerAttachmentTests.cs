using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.AI;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.AI.Providers;
using Vinx.Erp.AI.Providers.OpenAiCompatible;
using Vinx.Erp.AI.UnitTests.DocumentContext;
using Vinx.Erp.AI.UnitTests.Providers;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class AgentRunnerAttachmentTests
{
    private const int MainInputCharacterLimit = 96_000;

    private static readonly Guid CompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    [Fact]
    public async Task Profile_without_document_permission_rejects_attachments_before_the_provider()
    {
        var profile = Profile(allowDocumentAttachments: false);
        var model = TestAgentModel.Text("unused");
        var runner = new AgentRunner(
            new SequenceContextResolver(SequenceContextResolver.Resolved(CompanyId)),
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == profile.ProfileId ? profile : null);
        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            profile.ProfileId,
            "resuma",
            CompanyId,
            new[] { Guid.NewGuid() },
            CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Invalid, outcome.Status);
        Assert.Equal("attachments_not_allowed", outcome.ReasonCode);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task Missing_document_normalizer_needs_clarification_after_trusted_context_resolution()
    {
        var profile = Profile(allowDocumentAttachments: true);
        var resolver = new SequenceContextResolver(SequenceContextResolver.Resolved(CompanyId));
        var model = TestAgentModel.Text("unused");
        var runner = new AgentRunner(
            resolver,
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == profile.ProfileId ? profile : null);

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            profile.ProfileId,
            "resuma",
            CompanyId,
            new[] { Guid.NewGuid() },
            CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.NeedsClarification, outcome.Status);
        Assert.Equal("document_context_unavailable", outcome.ReasonCode);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task Three_attachments_reach_one_main_call_as_ordered_untrusted_normalized_context()
    {
        var ids = new[]
        {
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
        };
        var texts = new Dictionary<Guid, string>
        {
            [ids[0]] = "trecho alpha",
            [ids[1]] = "trecho beta",
            [ids[2]] = "trecho gamma",
        };
        var source = new FakeAuthorizedDocumentSource(id =>
            texts.TryGetValue(id, out var text)
                ? FakeAuthorizedDocumentSource.Clean(
                    id,
                    "application/pdf",
                    TestDocumentFixtures.PdfWithText(text))
                : null);
        var profile = Profile(allowDocumentAttachments: true);
        var resolver = new SequenceContextResolver(SequenceContextResolver.Resolved(CompanyId));
        var model = TestAgentModel.Text("resumo pronto");
        var runner = new AgentRunner(
            resolver,
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == profile.ProfileId ? profile : null,
            documentContextNormalizer: Normalizer(source));

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            profile.ProfileId,
            "resuma os anexos",
            CompanyId,
            ids,
            CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Completed, outcome.Status);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(3, source.OpenAttempts);
        Assert.Equal(1, model.CallCount);
        var turn = Assert.Single(Assert.Single(model.ReceivedMessages));
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.User, turn.Role);
        Assert.StartsWith("resuma os anexos", turn.Text, StringComparison.Ordinal);
        Assert.Contains("untrusted", turn.Text, StringComparison.Ordinal);
        AssertOrdered(turn.Text!, ids.Select(id => $"{id:N}:p1").ToArray());
        AssertOrdered(turn.Text!, ["trecho alpha", "trecho beta", "trecho gamma"]);
    }

    [Fact]
    public async Task One_maximal_attachment_with_boundary_message_is_admitted_once_by_the_real_adapter()
    {
        var id = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var maximalPages = Enumerable.Repeat<string?>(new string('a', 4_800), 20).ToArray();
        var maximalPdf = TestDocumentFixtures.MinimalPdf(maximalPages);
        var source = new FakeAuthorizedDocumentSource(candidate =>
            candidate == id
                ? FakeAuthorizedDocumentSource.Clean(
                    id,
                    "application/pdf",
                    maximalPdf)
                : null);
        var message = new string('m', 3_995) + "\"\\\n😀";
        var normalized = await Normalizer(source).BuildContextAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()), CompanyId, id, CancellationToken.None);
        Assert.True(normalized.Status is DocumentContextStatus.Ready, normalized.ClarificationReason);
        Assert.Equal(MainInputCharacterLimit, normalized.TotalCharacters);
        var (runner, handler) = RealAdapterRunner(source);

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            "test_summarize_v1",
            message,
            CompanyId,
            [id],
            CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Completed, outcome.Status);
        Assert.Equal(1, handler.CallCount);
        var turn = MainTurn(handler);
        Assert.True(turn.Length <= MainInputCharacterLimit, $"Main turn length was {turn.Length}.");
        Assert.StartsWith(message, turn, StringComparison.Ordinal);
        using var context = ContextJson(turn);
        var document = Assert.Single(context.RootElement.GetProperty("documents").EnumerateArray());
        Assert.True(document.GetProperty("truncated").GetBoolean());
        var finalText = document.GetProperty("segments").EnumerateArray().Last().GetProperty("text").GetString()!;
        Assert.False(finalText.Length > 0 && char.IsHighSurrogate(finalText[^1]));
    }

    [Fact]
    public async Task Three_large_attachments_are_ordered_and_admitted_once_by_the_real_adapter()
    {
        var ids = new[]
        {
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
        };
        var source = new FakeAuthorizedDocumentSource(id => ids.Contains(id)
            ? FakeAuthorizedDocumentSource.Clean(
                id,
                "application/pdf",
                TestDocumentFixtures.PdfWithText(
                    Enumerable.Repeat(new string('x', 4_000), 10).ToArray()))
            : null);
        var (runner, handler) = RealAdapterRunner(source);

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            "test_summarize_v1",
            "resuma em ordem",
            CompanyId,
            ids,
            CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Completed, outcome.Status);
        Assert.Equal(1, handler.CallCount);
        var turn = MainTurn(handler);
        Assert.True(turn.Length <= MainInputCharacterLimit, $"Main turn length was {turn.Length}.");
        AssertOrdered(turn, ids.Select(id => $"{id:N}:p1").ToArray());
        using var context = ContextJson(turn);
        var documents = context.RootElement.GetProperty("documents").EnumerateArray().ToArray();
        Assert.Equal(3, documents.Length);
        Assert.True(documents[^1].GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Aggregate_truncation_never_splits_a_surrogate_pair()
    {
        var id = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var text = string.Concat(Enumerable.Repeat("😀\"\\\n", 24_000));
        var context = new DocumentContextResult(
            id,
            DocumentContextStatus.Ready,
            ClarificationReason: null,
            [new DocumentSegment(1, $"{id:N}:p1", null, 1, DocumentSegmentSource.TextLayer, text)],
            text.Length,
            Truncated: false);
        var compose = typeof(AgentRunner).GetMethod(
            "ComposeTurn",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Agent turn composer was not found.");

        var turn = Assert.IsType<string>(compose.Invoke(
            null,
            ["resuma", new[] { context }, MainInputCharacterLimit]));

        Assert.True(turn.Length <= MainInputCharacterLimit, $"Main turn length was {turn.Length}.");
        using var json = ContextJson(turn);
        var finalText = json.RootElement.GetProperty("documents")[0]
            .GetProperty("segments")[0]
            .GetProperty("text")
            .GetString()!;
        Assert.False(finalText.Length > 0 && char.IsHighSurrogate(finalText[^1]));
        Assert.True(json.RootElement.GetProperty("documents")[0].GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Per_segment_truncation_sets_the_context_truncated_flag()
    {
        var id = Guid.NewGuid();
        var source = new FakeAuthorizedDocumentSource(
            _ => FakeAuthorizedDocumentSource.Clean(
                id,
                "image/png",
                TestDocumentFixtures.Png(10, 10)));
        var handler = TestProviderHandler.RespondsWith(
            TestProviderHandler.ChatCompletionJson(JsonSerializer.Serialize(new
            {
                text = "abc😀",
                confidence = 0.9,
                language = "pt",
            })));
        var normalizer = Normalizer(source, handler, limits =>
        {
            limits.MaxCharactersPerSegment = 4;
            limits.MaxCharactersPerContext = 100;
        });

        var result = await normalizer.BuildContextAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            Guid.NewGuid(),
            id,
            CancellationToken.None);

        Assert.Equal(DocumentContextStatus.Ready, result.Status);
        Assert.True(result.Truncated);
        Assert.Equal(3, result.TotalCharacters);
        Assert.Equal("abc", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public async Task Per_segment_truncation_is_advertised_in_the_composed_context()
    {
        var id = Guid.NewGuid();
        var source = new FakeAuthorizedDocumentSource(
            _ => FakeAuthorizedDocumentSource.Clean(
                id,
                "image/png",
                TestDocumentFixtures.Png(10, 10)));
        var visionHandler = TestProviderHandler.RespondsWith(
            TestProviderHandler.ChatCompletionJson(JsonSerializer.Serialize(new
            {
                text = "abc😀",
                confidence = 0.9,
                language = "pt",
            })));
        var profile = Profile(allowDocumentAttachments: true);
        var model = TestAgentModel.Text("resumo pronto");
        var runner = new AgentRunner(
            new SequenceContextResolver(SequenceContextResolver.Resolved(CompanyId)),
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == profile.ProfileId ? profile : null,
            documentContextNormalizer: Normalizer(source, visionHandler, limits =>
            {
                limits.MaxCharactersPerSegment = 4;
                limits.MaxCharactersPerContext = 100;
            }));

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            profile.ProfileId,
            "resuma",
            CompanyId,
            [id],
            CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Completed, outcome.Status);
        var turn = Assert.Single(Assert.Single(model.ReceivedMessages)).Text!;
        using var json = ContextJson(turn);
        var document = json.RootElement.GetProperty("documents")[0];
        Assert.True(document.GetProperty("truncated").GetBoolean());
        Assert.Equal("abc", document.GetProperty("segments")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task One_attachment_reaches_the_same_single_turn_path()
    {
        var id = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var source = new FakeAuthorizedDocumentSource(candidate =>
            candidate == id
                ? FakeAuthorizedDocumentSource.Clean(
                    id,
                    "application/pdf",
                    TestDocumentFixtures.PdfWithText("trecho unico"))
                : null);
        var profile = Profile(allowDocumentAttachments: true);
        var model = TestAgentModel.Text("resumo pronto");
        var runner = new AgentRunner(
            new SequenceContextResolver(SequenceContextResolver.Resolved(CompanyId)),
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == profile.ProfileId ? profile : null,
            documentContextNormalizer: Normalizer(source));

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            profile.ProfileId,
            "resuma",
            CompanyId,
            [id],
            CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Completed, outcome.Status);
        Assert.Equal(1, source.OpenAttempts);
        Assert.Single(Assert.Single(model.ReceivedMessages));
    }

    [Fact]
    public async Task Later_attachment_failure_discards_prior_context_before_the_main_provider()
    {
        var first = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var unavailable = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var source = new FakeAuthorizedDocumentSource(id =>
            id == first
                ? FakeAuthorizedDocumentSource.Clean(
                    id,
                    "application/pdf",
                    TestDocumentFixtures.PdfWithText("trecho que nao pode vazar"))
                : null);
        var profile = Profile(allowDocumentAttachments: true);
        var model = TestAgentModel.Text("unused");
        var runner = new AgentRunner(
            new SequenceContextResolver(SequenceContextResolver.Resolved(CompanyId)),
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == profile.ProfileId ? profile : null,
            documentContextNormalizer: Normalizer(source));

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            profile.ProfileId,
            "resuma",
            CompanyId,
            [first, unavailable],
            CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.NeedsClarification, outcome.Status);
        Assert.Equal("document_context_unavailable", outcome.ReasonCode);
        Assert.Equal(2, source.OpenAttempts);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_during_attachment_normalization_propagates_without_main_response()
    {
        var id = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var source = new FakeAuthorizedDocumentSource(_ =>
            FakeAuthorizedDocumentSource.Clean(
                id,
                "application/pdf",
                TestDocumentFixtures.PdfWithText("cancelado")));
        var profile = Profile(allowDocumentAttachments: true);
        var model = TestAgentModel.Text("unused");
        var runner = new AgentRunner(
            new SequenceContextResolver(SequenceContextResolver.Resolved(CompanyId)),
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == profile.ProfileId ? profile : null,
            documentContextNormalizer: Normalizer(source));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            profile.ProfileId,
            "resuma",
            CompanyId,
            [id],
            cancellation.Token));

        Assert.Equal(0, model.CallCount);
    }

    private static AgentTaskProfile Profile(bool allowDocumentAttachments) =>
        AgentTaskProfile.CreateValidated(
            "test_summarize_v1",
            "summarize",
            AgentJourneyScope.Company,
            AgentCapabilities.Read,
            new AgentModelSelection("test-provider", "test-model"),
            TimeSpan.FromSeconds(30),
            turnLimit: 1,
            maxTotalTokens: 8_000,
            allowedTools: [],
            allowDocumentAttachments);

    private static (AgentRunner Runner, TestProviderHandler Handler) RealAdapterRunner(
        FakeAuthorizedDocumentSource source,
        TestProviderHandler? visionHandler = null)
    {
        var profile = Profile(allowDocumentAttachments: true);
        var handler = TestProviderHandler.RespondsWith(
            TestProviderHandler.ChatCompletionJson("resumo pronto"));
        var mainOptions = new OpenAiCompatibleOptions
        {
            Endpoint = new Uri("https://unit-test.invalid/v1/", UriKind.Absolute),
            ApiKey = Guid.NewGuid().ToString("N"),
            Model = "test-model",
            MaxInputCharacters = MainInputCharacterLimit,
        };
        var model = new ChatClientAgentModel(
            new OpenAiCompatibleChatClient(mainOptions, new HttpClient(handler)));
        return (
            new AgentRunner(
                new SequenceContextResolver(SequenceContextResolver.Resolved(CompanyId)),
                model,
                NullLogger<AgentRunner>.Instance,
                profileId => profileId == profile.ProfileId ? profile : null,
                documentContextNormalizer: Normalizer(source, visionHandler)),
            handler);
    }

    private static DocumentContextNormalizer Normalizer(
        FakeAuthorizedDocumentSource source,
        TestProviderHandler? handler = null,
        Action<DocumentContextLimits>? mutateLimits = null)
    {
        var visionProfile = DocumentContextLimitsTests.VisionProfile();
        var limits = DocumentContextLimits.FromVisionProfile(
            visionProfile,
            new HashSet<string>(StringComparer.Ordinal) { "fiscal" });
        mutateLimits?.Invoke(limits);
        return new DocumentContextNormalizer(
            source,
            new DocumentVisionOcr(new OpenAiCompatibleChatClient(
                visionProfile,
                new HttpClient(handler ?? TestProviderHandler.RespondsWith("{}")))),
            limits);
    }

    private static void AssertOrdered(string value, IReadOnlyList<string> expected)
    {
        var previous = -1;
        foreach (var item in expected)
        {
            var current = value.IndexOf(item, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{item}' after index {previous}.");
            previous = current;
        }
    }

    private static string MainTurn(TestProviderHandler handler)
    {
        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        return body.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetString()
            ?? throw new InvalidOperationException("The main request has no user turn.");
    }

    private static JsonDocument ContextJson(string turn)
    {
        const string begin = "--- BEGIN UNTRUSTED DOCUMENT CONTEXT ---\n";
        const string end = "\n--- END UNTRUSTED DOCUMENT CONTEXT ---";
        var start = turn.IndexOf(begin, StringComparison.Ordinal) + begin.Length;
        var finish = turn.LastIndexOf(end, StringComparison.Ordinal);
        return JsonDocument.Parse(turn[start..finish]);
    }
}
