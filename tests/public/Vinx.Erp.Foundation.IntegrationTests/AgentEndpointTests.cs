using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Vinx.Erp.AI;
using Vinx.Erp.ApiHost.Agent;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AgentEndpointTests
{
    private const string Journey = AgentTaskProfileCatalog.ReadOnlySummarizeProfileId;
    private const string EmailInText = "cliente@exemplo.com";
    private const string ModelText =
        "Resumo do dia: pedido 214 faturado e enviado para " + EmailInText + " às 14h.";

    [Fact]
    public async Task Valid_run_returns_result_with_redacted_text_and_correlation()
    {
        var model = ControlledAgentModel.Text(ModelText);
        await using var factory = await CreateConfiguredFactoryAsync(model);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewRequest(scenario.AlfaCompanyId, "resuma o expediente de hoje");

        var response = await factory.CreateClient().PostAsJsonAsync("/agent/runs", request);

        response.EnsureSuccessStatusCode();
        var result = await ReadResponseAsync(response);
        Assert.Equal("result", result.Status);
        Assert.Equal("ok", result.ReasonCode);
        // O texto liberado é sempre o redigido: o endereço nunca chega ao canal.
        Assert.Contains("[email-redigido]", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(EmailInText, result.Text, StringComparison.Ordinal);
        Assert.Null(result.Sources);
        Assert.NotEqual(Guid.Empty, result.CorrelationId);
        Assert.Equal(
            result.CorrelationId.ToString("D"),
            Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        Assert.Equal(1, model.CallCount);
        // A seleção de provider veio da composição server-side, nunca do request.
        var selection = Assert.Single(model.ReceivedSelections);
        Assert.Equal(AgentRunOptions.OpenAiCompatibleProvider, selection.ProviderId);
        Assert.Equal("test-agent-model", selection.ModelId);
    }

    [Fact]
    public async Task Empty_message_returns_validation_error_without_provider_call()
    {
        var model = ControlledAgentModel.Text(ModelText);
        await using var factory = await CreateConfiguredFactoryAsync(model);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewRequest(scenario.AlfaCompanyId, "   ");

        var response = await factory.CreateClient().PostAsJsonAsync("/agent/runs", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await ReadResponseAsync(response);
        Assert.Equal("validation_error", result.Status);
        Assert.Equal("message_empty", result.ReasonCode);
        Assert.Null(result.Text);
        Assert.Equal(0, model.CallCount);
        // A rejeição acontece no parser estrito do endpoint, antes do serviço:
        // nada é auditado nem persistido — igual aos demais 400 do contrato.
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.AuditEvents.Where(audit => audit.Action == "agent.run").ToListAsync());
        Assert.Empty(await administrator.IdempotencyRecords.ToListAsync());
    }

    [Fact]
    public async Task Unknown_json_field_is_rejected()
    {
        var model = ControlledAgentModel.Text(ModelText);
        await using var factory = await CreateConfiguredFactoryAsync(model);
        var scenario = await SeedScenarioAsync(factory);
        var payload = $$"""
            {"requestId":"{{Guid.NewGuid()}}","journey":"{{Journey}}","message":"resuma o dia","companyId":"{{scenario.AlfaCompanyId}}","extra":"campo"}
            """;

        var response = await factory.CreateClient().PostAsync(
            "/agent/runs", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await ReadResponseAsync(response);
        Assert.Equal("validation_error", result.Status);
        Assert.Equal("invalid_request", result.ReasonCode);
        Assert.Null(result.Text);
        Assert.Equal(0, model.CallCount);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.AuditEvents.Where(audit => audit.Action == "agent.run").ToListAsync());
        Assert.Empty(await administrator.IdempotencyRecords.ToListAsync());
    }

    [Fact]
    public async Task Message_too_long_is_rejected()
    {
        var model = ControlledAgentModel.Text(ModelText);
        await using var factory = await CreateConfiguredFactoryAsync(model);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewRequest(scenario.AlfaCompanyId, new string('a', AgentHttpContract.MaxMessageLength + 1));

        var response = await factory.CreateClient().PostAsJsonAsync("/agent/runs", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await ReadResponseAsync(response);
        Assert.Equal("validation_error", result.Status);
        Assert.Equal("message_too_long", result.ReasonCode);
        Assert.Equal(0, model.CallCount);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.AuditEvents.Where(audit => audit.Action == "agent.run").ToListAsync());
    }

    [Fact]
    public async Task Missing_company_for_company_scoped_journey_needs_clarification_on_every_retry()
    {
        var model = ControlledAgentModel.Text(ModelText);
        await using var factory = await CreateConfiguredFactoryAsync(model);
        await SeedScenarioAsync(factory);
        var request = NewRequest(companyId: null, "resuma o dia");

        var first = await factory.CreateClient().PostAsJsonAsync("/agent/runs", request);
        var retry = await factory.CreateClient().PostAsJsonAsync("/agent/runs", request);

        foreach (var response in new[] { first, retry })
        {
            response.EnsureSuccessStatusCode();
            var result = await ReadResponseAsync(response);
            Assert.Equal("needs_clarification", result.Status);
            Assert.Equal("company_required", result.ReasonCode);
            Assert.Null(result.Text);
        }

        // Sem empresa não há registro de idempotência (o escopo exige companyId):
        // cada retentativa re-audita o evento Global em vez de devolver replay.
        Assert.Equal(0, model.CallCount);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.IdempotencyRecords.ToListAsync());
        var audits = await administrator.AuditEvents.Where(audit => audit.Action == "agent.run").ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, audit => Assert.Equal(AuditScope.Global, audit.Scope));
        Assert.All(audits, audit => Assert.Equal("company_required", audit.Reason));
    }

    [Fact]
    public async Task Forged_company_denies_without_consulting_the_model()
    {
        var model = ControlledAgentModel.Text(ModelText);
        await using var factory = await CreateConfiguredFactoryAsync(model);
        var scenario = await SeedScenarioAsync(factory, unmemberedCompany: true);
        var request = NewRequest(scenario.ForgedCompanyId!.Value, "resuma o dia da empresa vizinha");

        var response = await factory.CreateClient().PostAsJsonAsync("/agent/runs", request);

        response.EnsureSuccessStatusCode();
        var result = await ReadResponseAsync(response);
        Assert.Equal("denied", result.Status);
        Assert.Equal("company_not_authorized", result.ReasonCode);
        Assert.Null(result.Text);
        // A negação acontece na resolução de contexto: o provider nunca é chamado.
        Assert.Equal(0, model.CallCount);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var denial = await administrator.AuditEvents.SingleAsync(audit => audit.Action == "agent.run");
        Assert.Equal(AuditOutcome.Denied, denial.Outcome);
        Assert.Equal("company_not_authorized", denial.Reason);
        Assert.Equal(AuditScope.Company, denial.Scope);
        Assert.DoesNotContain("resuma o dia da empresa vizinha", denial.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_request_is_unauthorized()
    {
        // Sem token o challenge é real: nenhum serviço de aplicação roda.
        await using var factory = await FoundationApiFactory.CreateWithJwtBearerAsync();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/agent/runs", NewRequest(companyId: null, "resuma o dia"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.UserSessions.ToListAsync());
        Assert.Empty(await administrator.AuditEvents.Where(audit => audit.Action == "agent.run").ToListAsync());
    }

    [Fact]
    public async Task Same_requestId_replays_same_body_without_a_second_call()
    {
        var model = ControlledAgentModel.Text(ModelText);
        await using var factory = await CreateConfiguredFactoryAsync(model);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewRequest(scenario.AlfaCompanyId, "resuma o expediente");

        var first = await factory.CreateClient().PostAsJsonAsync("/agent/runs", request);
        var replay = await factory.CreateClient().PostAsJsonAsync("/agent/runs", request);

        first.EnsureSuccessStatusCode();
        replay.EnsureSuccessStatusCode();
        var firstResult = await ReadResponseAsync(first);
        var replayResult = await ReadResponseAsync(replay);
        Assert.Equal(firstResult.Status, replayResult.Status);
        Assert.Equal(firstResult.ReasonCode, replayResult.ReasonCode);
        Assert.Equal(firstResult.Text, replayResult.Text);
        Assert.Equal(firstResult.CorrelationId, replayResult.CorrelationId);
        Assert.Equal(1, model.CallCount);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var record = await administrator.IdempotencyRecords.SingleAsync();
        Assert.Equal("agent.run", record.CommandName);
        Assert.Equal(request.RequestId, record.RequestId);
        Assert.Equal(scenario.AlfaCompanyId, record.CompanyId);
        // O replay devolve exatamente o ResultJson seguro persistido.
        Assert.Contains("[email-redigido]", record.ResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain(EmailInText, record.ResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("resuma o expediente", record.ResultJson, StringComparison.Ordinal);
        Assert.Single(await administrator.AuditEvents.Where(audit => audit.Action == "agent.run").ToListAsync());
    }

    [Fact]
    public async Task Different_payload_same_requestId_conflicts_409()
    {
        var model = ControlledAgentModel.Text(ModelText);
        await using var factory = await CreateConfiguredFactoryAsync(model);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewRequest(scenario.AlfaCompanyId, "resuma o expediente");
        var first = await factory.CreateClient().PostAsJsonAsync("/agent/runs", request);
        first.EnsureSuccessStatusCode();

        var conflict = await factory.CreateClient().PostAsJsonAsync(
            "/agent/runs", request with { Message = "resuma outro expediente" });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var result = await ReadResponseAsync(conflict);
        Assert.Equal("conflict", result.Status);
        Assert.Equal("idempotency_conflict", result.ReasonCode);
        Assert.Null(result.Text);
        Assert.Equal(1, model.CallCount);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Single(await administrator.IdempotencyRecords.ToListAsync());
        var audits = await administrator.AuditEvents
            .Where(audit => audit.Action == "agent.run")
            .OrderBy(audit => audit.OccurredAt)
            .ToListAsync();
        Assert.Equal([AuditOutcome.Success, AuditOutcome.Denied], audits.Select(audit => audit.Outcome).ToList());
        Assert.Equal("idempotency_conflict", audits[1].Reason);
        Assert.Equal(("conflict", "idempotency_conflict"), ReadAuditMetadata(audits[1]));
    }

    [Fact]
    public async Task Provider_not_configured_returns_503_provider_not_configured()
    {
        // Caminho de produção: sem override e sem provider configurado o runner
        // falha fechado — nenhuma chamada externa, nada persistido.
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var request = NewRequest(scenario.AlfaCompanyId, "resuma o expediente");

        var response = await factory.CreateClient().PostAsJsonAsync("/agent/runs", request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("provider_not_configured", error?.Code);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.IdempotencyRecords.ToListAsync());
        Assert.Empty(await administrator.AuditEvents.Where(audit => audit.Action == "agent.run").ToListAsync());
    }

    [Fact]
    public async Task Timeout_maps_to_504_agent_timeout()
    {
        // O atraso do fake excede o timeout do perfil aprovado (30s): o token
        // vinculado cancela a chamada e a fronteira devolve 504 agent_timeout.
        var model = ControlledAgentModel.Delayed(TimeSpan.FromSeconds(50));
        await using var factory = await CreateConfiguredFactoryAsync(model);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewRequest(scenario.AlfaCompanyId, "resuma um expediente muito longo");

        var response = await factory.CreateClient().PostAsJsonAsync("/agent/runs", request);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("agent_timeout", error?.Code);
        // Desfecho 5xx não é durável: o mesmo requestId pode retentar depois.
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.IdempotencyRecords.ToListAsync());
        Assert.Empty(await administrator.AuditEvents.Where(audit => audit.Action == "agent.run").ToListAsync());
    }

    [Fact]
    public async Task Requires_confirmation_returns_opaque_state_without_execution()
    {
        const string proposedAction = "send_invoice_email";
        var model = ControlledAgentModel.ToolCall(proposedAction);
        await using var factory = await CreateConfiguredFactoryAsync(model);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewRequest(scenario.AlfaCompanyId, "envie a fatura por e-mail");

        var response = await factory.CreateClient().PostAsJsonAsync("/agent/runs", request);

        response.EnsureSuccessStatusCode();
        var rawBody = await response.Content.ReadAsStringAsync();
        var result = await ReadResponseAsync(response);
        Assert.Equal("requires_confirmation", result.Status);
        Assert.Equal("tool_proposal_requires_confirmation", result.ReasonCode);
        // Estado opaco: sem texto e sem o nome da ação proposta em nenhum campo.
        Assert.Null(result.Text);
        Assert.DoesNotContain(proposedAction, rawBody, StringComparison.Ordinal);
        Assert.Equal(1, model.CallCount);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var record = await administrator.IdempotencyRecords.SingleAsync();
        Assert.DoesNotContain(proposedAction, record.ResultJson, StringComparison.Ordinal);
        var audit = await administrator.AuditEvents.SingleAsync(auditEvent => auditEvent.Action == "agent.run");
        Assert.Equal(AuditOutcome.Success, audit.Outcome);
        Assert.DoesNotContain(proposedAction, audit.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parallel_requests_isolate_outcomes_and_correlations()
    {
        // Mesmo ator, duas empresas autorizadas, chamadas concorrentes: cada
        // resposta, registro e auditoria fica na empresa do seu request.
        const string alfaText = "Resumo da empresa Alfa sem pendências.";
        const string betaText = "Resumo da empresa Beta com duas pendências.";
        var model = new ControlledAgentModel(TextResponse(alfaText), TextResponse(betaText));
        await using var factory = await CreateConfiguredFactoryAsync(model);
        var scenario = await SeedScenarioAsync(factory, authorizedCompanies: 2);
        var requests = new[]
        {
            NewRequest(scenario.AlfaCompanyId, "resuma a Alfa"),
            NewRequest(scenario.BetaCompanyId!.Value, "resuma a Beta")
        };

        var responses = await Task.WhenAll(requests.Select(request =>
            factory.CreateClient().PostAsJsonAsync("/agent/runs", request)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var results = await Task.WhenAll(responses.Select(ReadResponseAsync));
        Assert.All(results, result => Assert.Equal("result", result.Status));
        Assert.Equal(2, model.CallCount);
        Assert.NotEqual(results[0].CorrelationId, results[1].CorrelationId);
        // A ordem de chegada das respostas é da corrida: o conjunto de textos
        // recebido é exatamente o programado, sem mistura nem perda.
        Assert.Equal(new[] { alfaText, betaText }.Order(), results.Select(result => result.Text).Order());
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var records = await administrator.IdempotencyRecords.ToListAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal(
            new[] { scenario.AlfaCompanyId, scenario.BetaCompanyId!.Value }.Order(),
            records.Select(record => record.CompanyId).Order());
        Assert.Equal(new[] { alfaText, betaText }.Order(), records.Select(StoredResponseText).Order());
        var audits = await administrator.AuditEvents.Where(auditEvent => auditEvent.Action == "agent.run").ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Equal(
            new[] { scenario.AlfaCompanyId, scenario.BetaCompanyId }.Order(),
            audits.Select(audit => audit.RequestedCompanyId).Order());
    }

    [Fact]
    public async Task Audit_records_success_and_denied_with_safe_metadata_only()
    {
        var model = ControlledAgentModel.Text(ModelText);
        await using var factory = await CreateConfiguredFactoryAsync(model);
        var granted = await SeedScenarioAsync(factory);
        var ungranted = await SeedScenarioAsync(factory, grantRead: false);
        var successRequest = NewRequest(granted.AlfaCompanyId, "resuma o expediente de hoje");
        var deniedRequest = NewRequest(ungranted.AlfaCompanyId, "resuma o meu expediente");

        var success = await factory.CreateClient().PostAsJsonAsync("/agent/runs", successRequest);
        var denied = await factory.CreateClient().PostAsJsonAsync("/agent/runs", deniedRequest);

        success.EnsureSuccessStatusCode();
        denied.EnsureSuccessStatusCode();
        Assert.Equal("result", (await ReadResponseAsync(success)).Status);
        Assert.Equal("denied", (await ReadResponseAsync(denied)).Status);
        Assert.Equal(1, model.CallCount);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var successAudit = await administrator.AuditEvents.SingleAsync(audit =>
            audit.Action == "agent.run" && audit.Outcome == AuditOutcome.Success);
        Assert.Equal(AuditSource.Ai, successAudit.Source);
        Assert.Equal(AuditScope.Company, successAudit.Scope);
        Assert.Equal(granted.AlfaCompanyId, successAudit.RequestedCompanyId);
        Assert.Equal(successRequest.RequestId, successAudit.ResourceId);
        Assert.Equal("ok", successAudit.Reason);
        Assert.Equal(("result", "ok"), ReadAuditMetadata(successAudit));
        Assert.DoesNotContain(EmailInText, successAudit.MetadataJson, StringComparison.Ordinal);
        var denial = await administrator.AuditEvents.SingleAsync(audit =>
            audit.Action == "agent.run" && audit.Outcome == AuditOutcome.Denied);
        Assert.Equal("capability_denied", denial.Reason);
        Assert.Equal(ungranted.AlfaCompanyId, denial.RequestedCompanyId);
        Assert.Equal(("denied", "capability_denied"), ReadAuditMetadata(denial));
        Assert.DoesNotContain("resuma o meu expediente", denial.MetadataJson, StringComparison.Ordinal);
        // A negação também persiste idempotência (resposta tipada): o ResultJson
        // carrega somente a resposta segura, nunca o prompt.
        var records = await administrator.IdempotencyRecords.ToListAsync();
        var deniedRecord = Assert.Single(records, record => record.CompanyId == ungranted.AlfaCompanyId);
        Assert.DoesNotContain("resuma o meu expediente", deniedRecord.ResultJson, StringComparison.Ordinal);
    }

    private static async Task<FoundationApiFactory> CreateConfiguredFactoryAsync(ControlledAgentModel model) =>
        await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            agentModel: model,
            agentRunOptions: new AgentRunOptions
            {
                Provider = AgentRunOptions.OpenAiCompatibleProvider,
                Endpoint = new Uri("http://127.0.0.1:9999"),
                ApiKey = "test-agent-key",
                Model = "test-agent-model"
            });

    private static AgentRunRequest NewRequest(Guid? companyId, string message) =>
        new(Guid.NewGuid(), Journey, message, companyId);

    private static async Task<AgentRunResponse> ReadResponseAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<AgentRunResponse>()
            ?? throw new InvalidOperationException("The agent run response body was not readable.");

    // A auditoria carrega somente {status, reasonCode}; o parse evita acoplar o
    // teste à formatação exata do JSON serializado.
    private static (string Status, string? ReasonCode) ReadAuditMetadata(AuditEvent audit)
    {
        using var document = JsonDocument.Parse(audit.MetadataJson);
        return (
            document.RootElement.GetProperty("Status").GetString()
                ?? throw new InvalidOperationException("The audit metadata has no status."),
            document.RootElement.TryGetProperty("ReasonCode", out var reasonCode) ? reasonCode.GetString() : null);
    }

    // Texto seguro armazenado no registro de idempotência (ResultJson → Response → Text).
    private static string? StoredResponseText(IdempotencyRecord record)
    {
        using var document = JsonDocument.Parse(record.ResultJson);
        return document.RootElement.GetProperty("Response").GetProperty("Text").GetString();
    }

    private static async Task<AgentScenario> SeedScenarioAsync(
        FoundationApiFactory factory,
        int authorizedCompanies = 1,
        bool grantRead = true,
        bool unmemberedCompany = false)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var request = Guid.NewGuid();
        var readCapability = await administrator.Capabilities.SingleOrDefaultAsync(capability =>
            capability.Name == AgentCapabilities.Read);
        if (readCapability is null)
        {
            readCapability = new Capability(Guid.NewGuid(), AgentCapabilities.Read);
            administrator.Capabilities.Add(readCapability);
        }

        var group = new Group(Guid.NewGuid(), $"Vinx Group {request:N}", factory.ActorUserId, request);
        administrator.Add(group);
        Guid? alfaCompanyId = null;
        Guid? betaCompanyId = null;
        for (var index = 0; index < authorizedCompanies; index++)
        {
            var companyId = SeedAuthorizedCompany(
                administrator, group, index == 0 ? $"Alfa {request:N}" : $"Beta {request:N}", readCapability.Id,
                factory.ActorUserId, request, grantRead);
            if (index == 0)
            {
                alfaCompanyId = companyId;
            }
            else
            {
                betaCompanyId = companyId;
            }
        }

        Guid? forgedCompanyId = null;
        if (unmemberedCompany)
        {
            var forged = new Company(Guid.NewGuid(), group.Id, $"Vinx Forjada {request:N}", factory.ActorUserId, request);
            administrator.Add(forged);
            forgedCompanyId = forged.Id;
        }

        await administrator.SaveChangesAsync();
        return new AgentScenario(alfaCompanyId!.Value, betaCompanyId, forgedCompanyId);
    }

    private static Guid SeedAuthorizedCompany(
        FoundationDbContext administrator,
        Group group,
        string name,
        Guid readCapabilityId,
        Guid actorUserId,
        Guid request,
        bool grantRead)
    {
        var company = new Company(Guid.NewGuid(), group.Id, $"Vinx {name}", actorUserId, request);
        var membership = new Membership(Guid.NewGuid(), company.Id, actorUserId, actorUserId, request);
        var role = new Role(Guid.NewGuid(), company.Id, $"Agent operator {name}", actorUserId, request);
        var assignment = new RoleAssignment(Guid.NewGuid(), company.Id, role.Id, membership.Id, actorUserId, request);
        administrator.AddRange(company, membership, role, assignment);
        if (grantRead)
        {
            administrator.RoleCapabilities.Add(new RoleCapability(
                Guid.NewGuid(), company.Id, role.Id, readCapabilityId, CapabilityScope.Company, actorUserId, request));
        }

        return company.Id;
    }

    private static ChatResponse TextResponse(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private sealed record AgentScenario(Guid AlfaCompanyId, Guid? BetaCompanyId, Guid? ForgedCompanyId);

    private sealed record AgentRunRequest(Guid RequestId, string Journey, string Message, Guid? CompanyId);

    private sealed record AgentRunResponse(
        string Status,
        string? ReasonCode,
        string? Text,
        IReadOnlyList<AgentSourceRef>? Sources,
        Guid CorrelationId);

    private sealed record AgentSourceRef(string Reference);

    private sealed record ErrorResponse(string Code);

    /// <summary>
    /// Fake de <see cref="IAgentModel"/> no padrão do ControlledMalwareScanner:
    /// respostas programáveis em sequência, atraso sensível ao token (timeout do
    /// perfil aprovado) e registro das selections recebidas. Nenhum HTTP.
    /// </summary>
    private sealed class ControlledAgentModel : IAgentModel
    {
        private readonly Queue<ChatResponse> responses;
        private int callCount;

        public ControlledAgentModel(params ChatResponse[] responses) =>
            this.responses = new Queue<ChatResponse>(responses);

        public TimeSpan? Delay { get; init; }

        public int CallCount => Volatile.Read(ref callCount);

        public List<AgentModelSelection> ReceivedSelections { get; } = [];

        public static ControlledAgentModel Text(string text) => new(TextResponse(text));

        public static ControlledAgentModel ToolCall(string toolName) => new(new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            new AIContent[] { new FunctionCallContent("call_1", toolName, new Dictionary<string, object?>()) })));

        public static ControlledAgentModel Delayed(TimeSpan delay) => new(TextResponse("resposta atrasada"))
        {
            Delay = delay
        };

        public async Task<ChatResponse> GetResponseAsync(
            IReadOnlyList<ChatMessage> messages,
            AgentModelSelection selection,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            ReceivedSelections.Add(selection);
            if (Delay is { } delay)
            {
                await Task.Delay(delay, cancellationToken);
            }

            if (responses.Count == 0)
            {
                throw new InvalidOperationException("The fake agent model has no response left.");
            }

            return responses.Dequeue();
        }
    }
}
