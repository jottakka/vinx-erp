using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.AI;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.AI.Providers.OpenAiCompatible;
using Vinx.Erp.AI.UnitTests.DocumentContext;
using Vinx.Erp.AI.UnitTests.Providers;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class AgentRunnerAttachmentTests
{
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

    private static DocumentContextNormalizer Normalizer(FakeAuthorizedDocumentSource source)
    {
        var visionProfile = DocumentContextLimitsTests.VisionProfile();
        var limits = DocumentContextLimits.FromVisionProfile(
            visionProfile,
            new HashSet<string>(StringComparer.Ordinal) { "fiscal" });
        return new DocumentContextNormalizer(
            source,
            new DocumentVisionOcr(new OpenAiCompatibleChatClient(
                visionProfile,
                new HttpClient(TestProviderHandler.RespondsWith("{}")))),
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
}
