using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.AI;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.AI.Guardrails;
using Vinx.Erp.AI.Providers.OpenAiCompatible;
using Vinx.Erp.AI.UnitTests.DocumentContext;
using Vinx.Erp.AI.UnitTests.Providers;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

// Integração runner + gate de saída: o gate só roda quando houve resposta do
// modelo; sem gate configurado o comportamento é o atual. Fixtures sintéticas
// (.example) — nenhum dado pessoal real.
public sealed class AgentRunnerGateIntegrationTests
{
    private static readonly Guid AlfaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static readonly Guid PaguaiFileId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public async Task Gate_result_maps_to_completed_with_model_text()
    {
        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());
        var (runner, model) = CreateRunner(
            TestAgentModel.Text("Resumo enviado para joao.silva@example.com ontem"),
            gate: Gate());

        var outcome = await runner.RunAsync(actor, TestProfileId, "resuma X", AlfaCompanyId, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Completed, outcome.Status);
        Assert.Equal(AgentOutputGate.Ok, outcome.ReasonCode);
        // O gate não redige: o texto do modelo atravessa sem transformação.
        Assert.NotNull(outcome.Text);
        Assert.Contains("joao.silva@example.com", outcome.Text, StringComparison.Ordinal);
        Assert.Equal(actor.CorrelationId, outcome.CorrelationId);
        Assert.True(outcome.Elapsed >= TimeSpan.Zero);
        Assert.Equal(1, model.CallCount);
    }

    [Fact]
    public async Task Tool_proposal_with_gate_keeps_requires_confirmation_with_model_text()
    {
        var (runner, _) = CreateRunner(
            new TestAgentModel(TextWithToolCall("Resumo para joao.silva@example.com", "tool_x")),
            gate: Gate());

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()), TestProfileId, "resuma X", AlfaCompanyId, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.RequiresConfirmation, outcome.Status);
        Assert.Equal(AgentOutcomeReason.ToolProposalRequiresConfirmation, outcome.ReasonCode);
        Assert.Equal("tool_x", outcome.ProposedActionName);
        Assert.NotNull(outcome.Text);
        Assert.Contains("joao.silva@example.com", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_proposal_without_text_keeps_requires_confirmation_when_gate_abstains()
    {
        // Resposta só com tool call: o gate se abstém por falta de evidência,
        // mas a proposta domina — RequiresConfirmation é preservado.
        var (runner, _) = CreateRunner(TestAgentModel.ToolCall("tool_x"), gate: Gate());

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()), TestProfileId, "resuma X", AlfaCompanyId, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.RequiresConfirmation, outcome.Status);
        Assert.Equal(AgentOutcomeReason.ToolProposalRequiresConfirmation, outcome.ReasonCode);
        Assert.Equal("tool_x", outcome.ProposedActionName);
        Assert.Null(outcome.Text);
    }

    [Fact]
    public async Task Sources_required_profile_without_sources_needs_clarification()
    {
        // V1 o modelo não cita fontes estruturadas: SourcesRequired true no
        // perfil conduz à abstenção no_authorized_source via contrato do perfil.
        var (runner, _) = CreateRunner(
            TestAgentModel.Text("Resumo Alfa"),
            profile: TestProfile(sourcesRequired: true),
            gate: Gate());

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()), TestProfileId, "resuma X", AlfaCompanyId, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.NeedsClarification, outcome.Status);
        Assert.Equal(AgentOutputGate.NoAuthorizedSource, outcome.ReasonCode);
        Assert.Null(outcome.Text);
    }

    [Fact]
    public async Task Missing_required_output_field_is_invalid()
    {
        // StructuredJson é sempre null na v1: contrato com obrigatórios reprova
        // fechado antes de expor qualquer texto.
        var (runner, _) = CreateRunner(
            TestAgentModel.Text("Resumo Alfa"),
            profile: TestProfile(requiredOutputFields: new HashSet<string> { "titulo" }),
            gate: Gate());

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()), TestProfileId, "resuma X", AlfaCompanyId, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Invalid, outcome.Status);
        Assert.Equal(AgentOutputGate.SchemaInvalid, outcome.ReasonCode);
        Assert.Null(outcome.Text);
    }

    [Fact]
    public async Task Without_gate_behavior_is_unchanged()
    {
        var (runner, _) = CreateRunner(TestAgentModel.Text("Resumo para joao.silva@example.com"));

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()), TestProfileId, "resuma X", AlfaCompanyId, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Completed, outcome.Status);
        Assert.Equal(AgentOutcomeReason.Completed, outcome.ReasonCode);
        Assert.Equal("Resumo para joao.silva@example.com", outcome.Text);
    }

    [Fact]
    public async Task Gate_does_not_run_without_model_response()
    {
        // Caminho curto de contexto negado: mesmo com gate configurado, o
        // desfecho é o da resolução e o modelo não é chamado.
        var resolver = new SequenceContextResolver(new AgentContextResolution(
            AgentContextResolutionStatus.Denied,
            AgentOutcomeReason.CompanyNotAuthorized));
        var model = TestAgentModel.Text("Resumo Alfa");
        var runner = new AgentRunner(
            resolver,
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == TestProfileId ? TestProfile() : null,
            outputGate: Gate());

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()), TestProfileId, "resuma X", AlfaCompanyId, CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Denied, outcome.Status);
        Assert.Equal(AgentOutcomeReason.CompanyNotAuthorized, outcome.ReasonCode);
        Assert.Null(outcome.Text);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public void Paguai_document_contract_releases_a_typed_proposal_without_the_raw_model_json()
    {
        const string structuredJson = """
            {
              "status": "proposal",
              "reason": null,
              "proposal": {
                "title": "Fatura de energia",
                "description": "Pagamento mensal",
                "amount": "250.00",
                "currency": "BRL",
                "dueDate": "2026-09-10",
                "beneficiaryDisplayName": "Companhia de energia",
                "proofRequirement": "optional",
                "invoiceFileId": "11111111-1111-4111-8111-111111111111",
                "confidence": 0.90,
                "sourceReferences": [
                  { "field": "amount", "source": "11111111111141118111111111111111:p1" },
                  { "field": "dueDate", "source": "11111111111141118111111111111111:p1" },
                  { "field": "beneficiaryDisplayName", "source": "11111111111141118111111111111111:p1" }
                ]
              }
            }
            """;
        var request = new AgentOutputGateRequest(
            Context(),
            new AgentModelResponse(
                Text: null,
                StructuredJson: structuredJson,
                CitedSources: [],
                Confidence: null,
                HasToolProposal: false),
            new AgentOutputContract(AgentTaskProfileCatalog.PaguaiFromDocumentProfileId, new HashSet<string>()),
            SourcesRequired: false,
            new PaguaiDocumentProposalParseRequest(
                PaguaiFileId,
                new HashSet<string>(StringComparer.Ordinal) { "11111111111141118111111111111111:p1" },
                structuredJson));

        var result = Gate().Evaluate(request);

        Assert.Equal(AgentOutputGateStatus.Result, result.Status);
        Assert.Equal(AgentOutputGate.Ok, result.ReasonCode);
        Assert.Null(result.Text);
        var proposal = Assert.IsType<PaguaiDocumentProposal>(result.PaguaiProposal);
        Assert.Equal(250m, proposal.Amount);
        Assert.Equal(PaguaiFileId, proposal.InvoiceFileId);
        Assert.DoesNotContain(structuredJson, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paguai_profile_requires_exactly_one_attachment_before_context_or_model()
    {
        Assert.True(AgentTaskProfileCatalog.TryGet(AgentTaskProfileCatalog.PaguaiFromDocumentProfileId, out var profile));
        var resolver = new SequenceContextResolver(SequenceContextResolver.Resolved(AlfaCompanyId));
        var model = TestAgentModel.Text("unused");
        var runner = new AgentRunner(
            resolver,
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == profile!.ProfileId ? profile : null,
            outputGate: Gate());

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            AgentTaskProfileCatalog.PaguaiFromDocumentProfileId,
            "extraia a fatura",
            AlfaCompanyId,
            Array.Empty<Guid>(),
            CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Invalid, outcome.Status);
        Assert.Equal("exactly_one_attachment_required", outcome.ReasonCode);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task Paguai_structured_response_completes_with_only_the_typed_proposal()
    {
        const string structuredJson = """
            {
              "status": "proposal",
              "reason": null,
              "proposal": {
                "title": "Fatura de energia",
                "description": "Pagamento mensal",
                "amount": "250.00",
                "currency": "BRL",
                "dueDate": "2026-09-10",
                "beneficiaryDisplayName": "Companhia de energia",
                "proofRequirement": "optional",
                "invoiceFileId": "11111111-1111-4111-8111-111111111111",
                "confidence": 0.90,
                "sourceReferences": [
                  { "field": "amount", "source": "11111111111141118111111111111111:p1" },
                  { "field": "dueDate", "source": "11111111111141118111111111111111:p1" },
                  { "field": "beneficiaryDisplayName", "source": "11111111111141118111111111111111:p1" }
                ]
              }
            }
            """;
        var source = new FakeAuthorizedDocumentSource(id => id == PaguaiFileId
            ? FakeAuthorizedDocumentSource.Clean(id, "application/pdf", TestDocumentFixtures.PdfWithText("fatura"))
            : null);
        var profile = PaguaiProfile();
        var model = TestAgentModel.Text(structuredJson);
        var runner = new AgentRunner(
            new SequenceContextResolver(SequenceContextResolver.Resolved(AlfaCompanyId)),
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == profile.ProfileId ? profile : null,
            outputGate: Gate(),
            documentContextNormalizer: Normalizer(source));

        var outcome = await runner.RunAsync(
            new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
            profile.ProfileId,
            "extraia a fatura",
            AlfaCompanyId,
            [PaguaiFileId],
            CancellationToken.None);

        Assert.Equal(AgentOutcomeStatus.Completed, outcome.Status);
        Assert.Equal(AgentOutputGate.Ok, outcome.ReasonCode);
        Assert.Equal(AgentPolicyVersion.Current, outcome.PolicyVersion);
        Assert.Null(outcome.Text);
        var proposal = Assert.IsType<PaguaiDocumentProposal>(outcome.PaguaiProposal);
        Assert.Equal(250m, proposal.Amount);
        Assert.Equal(PaguaiFileId, proposal.InvoiceFileId);
        Assert.DoesNotContain(structuredJson, outcome.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, model.CallCount);
    }

    private const string TestProfileId = "test_summarize_v1";

    private static AgentOutputGate Gate() => new(new SourceAuthorizationGate());

    private static AgentExecutionContext Context() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        AlfaCompanyId,
        "Alfa",
        Guid.NewGuid(),
        AgentTaskProfileCatalog.PaguaiFromDocumentProfileId,
        new HashSet<string>());

    private static AgentTaskProfile TestProfile(
        bool sourcesRequired = false,
        IReadOnlySet<string>? requiredOutputFields = null) => new(
        TestProfileId,
        "summarize",
        AgentJourneyScope.Company,
        AgentCapabilities.Read,
        new AgentModelSelection("test-provider", "test-model"),
        TimeSpan.FromSeconds(30),
        TurnLimit: 1,
        MaxTotalTokens: 8_000,
        AllowedTools: [],
        SourcesRequired: sourcesRequired,
        RequiredOutputFields: requiredOutputFields);

    private static AgentTaskProfile PaguaiProfile() => new(
        AgentTaskProfileCatalog.PaguaiFromDocumentProfileId,
        AgentTaskProfileCatalog.PaguaiFromDocumentProfileId,
        AgentJourneyScope.Company,
        AgentCapabilities.Read,
        new AgentModelSelection("test-provider", "test-model"),
        TimeSpan.FromSeconds(30),
        TurnLimit: 1,
        MaxTotalTokens: 8_000,
        AllowedTools: [],
        AllowDocumentAttachments: true);

    private static DocumentContextNormalizer Normalizer(FakeAuthorizedDocumentSource source)
    {
        var visionProfile = DocumentContextLimitsTests.VisionProfile();
        return new DocumentContextNormalizer(
            source,
            new DocumentVisionOcr(new OpenAiCompatibleChatClient(
                visionProfile,
                new HttpClient(TestProviderHandler.RespondsWith("{}")))),
            DocumentContextLimits.FromVisionProfile(
                visionProfile,
                new HashSet<string>(StringComparer.Ordinal) { "fiscal" }));
    }

    private static (AgentRunner Runner, TestAgentModel Model) CreateRunner(
        TestAgentModel model,
        AgentTaskProfile? profile = null,
        AgentOutputGate? gate = null)
    {
        var resolvedProfile = profile ?? TestProfile();
        var runner = new AgentRunner(
            new SequenceContextResolver(SequenceContextResolver.Resolved(AlfaCompanyId)),
            model,
            NullLogger<AgentRunner>.Instance,
            profileId => profileId == resolvedProfile.ProfileId ? resolvedProfile : null,
            outputGate: gate);
        return (runner, model);
    }

    private static ChatResponse TextWithToolCall(string text, string toolName) => new(new ChatMessage(
        ChatRole.Assistant,
        [
            new TextContent(text),
            new FunctionCallContent("call_1", toolName, new Dictionary<string, object?>()),
        ]));
}
