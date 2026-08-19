# Agente — harness e contexto confiável (#127 + #132) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** recriar a base mínima hospedada do agente (#127 + #132): módulo `Vinx.Erp.AI` com contexto confiável server-side e perfil de tarefa aprovado, sem provider real, UI, WhatsApp ou mutação.

**Architecture:** módulo AI consumível por API, Worker e futuros canais. `ActorContext`, membership, empresa explícita e capabilities vêm da Foundation (`AuthorizedCompanySetResolver` já existente — não duplicar RLS). O agente recebe apenas um snapshot mínimo; mensagem, modelo, canal e argumento do cliente nunca escolhem autorização, empresa ou provider. A política VINX roda inteira antes de qualquer chamada de modelo.

**Tech Stack:** .NET 10, `Microsoft.Extensions.AI.Abstractions` 10.8.3 (`ChatMessage`/`ChatResponse`/`IChatClient` como fronteira de normalização de turns/outcomes), EF/Npgsql não necessário nesta PR, testes públicos xUnit.

**Spec:** `docs/handoffs/agentic-mvp/priority-agent-whatsapp/agent-harness-context.md` (handoff) · `docs/superpowers/specs/2026-08-11-agent-framework-boundaries-design.md` (invariantes de segurança, seção 4) · `docs/superpowers/specs/2026-08-15-backend-first-simplification.md` (por que o runtime foi removido).

## Global Constraints

- Ordem inegociável: contexto confiável + perfil aprovado resolvem-se ANTES de qualquer chamada de modelo/tool; nenhum teste pode chamar o modelo em caminho de falha.
- A resposta do modelo nunca é autorização; NENHUMA tool é chamada/executada nesta PR — proposta de tool vira `requires_confirmation`.
- Mensagem, modelo, canal e argumento do cliente nunca escolhem autorização, empresa ou provider; valores de perfil vindos do cliente são rejeitados por construção (perfil só existe no catálogo server-side).
- `AgentExecutionContext` não contém prompt bruto, JWT, e-mail ou segredo; snapshot mínimo (ator, empresa, capabilities, correlation ID, perfil).
- Sem loop de chat, dispatcher de tool, workflow, checkpoint ou serializador de provider próprio: exatamente UMA chamada de modelo por execução (turn limit 1).
- Logs/métricas somente com operação, módulo, resultado, motivo seguro, duração e correlation ID; nunca prompt, resposta do modelo ou PII.
- Modificar `src/Foundation/Vinx.Erp.Foundation/` SOMENTE se imprescindível para consumir o resolver existente; nunca duplicar RLS. **Ruling desta PR: Foundation intocada.**
- ApiHost, WorkerHost, frontend e WhatsApp ficam fora; não tocar `quality/private`.
- Um arquivo por classe/família coesa (precedente: `ActorContext.cs` e `AuthorizedCompanySetResolver.cs` co-localizam tipos pequenos relacionados); net10.0; `TreatWarningsAsErrors`; lockfiles (`packages.lock.json`) commitados; `dotnet format whitespace --verify-no-changes` limpo.
- Pacote novo autorizado: somente `Microsoft.Extensions.AI.Abstractions` 10.8.3 em `Directory.Packages.props` (consumidor: `Vinx.Erp.AI`; justificar no PR). Sem dependência sem uso atual.
- Documentação em português; XML `<summary>` compacto em métodos públicos cuja garantia não é óbvia.
- Branch: `codex/issue-127-132-agent-harness-context` a partir de `origin/main` (64b565a). Worktree: `.worktrees/agent-harness-context`.

## Estimativa

~28 caminhos (module ~14, testes ~9, arch test 1, docs 2, props/sln + lockfiles gerados). Excede o alvo de 25 arquivos por causa de tipos pequenos (enums/records de 1–10 linhas); a fatia é coesa (um único módulo novo + seus testes) e a justificativa fica registrada aqui e no PR. ~700–900 linhas manuais estimadas.

---

### Task 1: Scaffold do módulo e regras de arquitetura

**Files:**
- Create: `src/Modules/Vinx.Erp.AI/Vinx.Erp.AI.csproj`
- Create: `tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj`
- Modify: `Vinx.Erp.sln` (`dotnet sln add` dos dois projetos)
- Modify: `Directory.Packages.props` (adicionar `Microsoft.Extensions.AI.Abstractions` 10.8.3)
- Create: `tests/public/Vinx.Erp.ArchitectureTests/AiModuleDependencyTests.cs`
- Generated: `packages.lock.json` dos dois projetos novos (commit)

**Interfaces:**
- Produces: projetos `Vinx.Erp.AI` e `Vinx.Erp.AI.UnitTests` compilando na solução; `Vinx.Erp.AI` referencia `Vinx.Erp.Foundation` (ProjectReference) e `Microsoft.Extensions.AI.Abstractions`; teste de arquitetura afirma que módulos de domínio não dependem do AI.

- [ ] **Step 1: RED — teste de arquitetura**

`tests/public/Vinx.Erp.ArchitectureTests/AiModuleDependencyTests.cs` — **decisão:** sem marcadores artificiais e sem `Assembly.LoadFile` (frágil); o teste lê os `.csproj` reais (determinístico, mesmo acesso a `RepositoryRoot()` já usado por `ModuleContractDocumentationTests`) e afirma as referências permitidas/proibidas:

```csharp
using Vinx.Erp.Foundation.Authorization;
using Xunit;

namespace Vinx.Erp.ArchitectureTests;

public sealed class AiModuleDependencyTests
{
    private static string RepositoryRoot() =>
        // mesmo padrão privado já usado por ModuleContractDocumentationTests
        ...;

    private static string ReadProject(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));

    [Fact]
    public void Foundation_references_are_unchanged() // AI não entra na Foundation
    {
        var foundation = ReadProject("src/Foundation/Vinx.Erp.Foundation/Vinx.Erp.Foundation.csproj");
        Assert.DoesNotContain("Vinx.Erp.AI", foundation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/Modules/Vinx.Erp.Paguai/Vinx.Erp.Paguai.csproj")]
    [InlineData("src/Modules/Vinx.Erp.Atendimento/Vinx.Erp.Atendimento.csproj")]
    public void Domain_modules_do_not_reference_ai(string projectPath)
    {
        Assert.DoesNotContain("Vinx.Erp.AI.csproj", ReadProject(projectPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_module_references_foundation_and_no_hosts()
    {
        var ai = ReadProject("src/Modules/Vinx.Erp.AI/Vinx.Erp.AI.csproj");
        Assert.Contains("Vinx.Erp.Foundation.csproj", ai, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiHost", ai, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkerHost", ai, StringComparison.Ordinal);
        Assert.DoesNotContain("McpHost", ai, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run RED** — `dotnet test tests/public/Vinx.Erp.ArchitectureTests` → falha (`Ai_module_references_foundation_and_no_hosts`: csproj do AI não existe).

- [ ] **Step 3: GREEN — scaffolding**

`src/Modules/Vinx.Erp.AI/Vinx.Erp.AI.csproj` (espelha `Vinx.Erp.Atendimento.csproj`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../../Foundation/Vinx.Erp.Foundation/Vinx.Erp.Foundation.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" />
  </ItemGroup>
</Project>
```

`tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj` (espelha `Vinx.Erp.Foundation.UnitTests.csproj`; ProjectReference aponta para o módulo AI):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../../src/Modules/Vinx.Erp.AI/Vinx.Erp.AI.csproj" />
  </ItemGroup>
</Project>
```

`Directory.Packages.props` (ItemGroup existente, ordem alfabética):

```xml
    <PackageVersion Include="Microsoft.Extensions.AI.Abstractions" Version="10.8.3" />
```

Adicionar um arquivo mínimo temporário? NÃO — csproj vazio compila sem .cs. Registrar na solução e gerar lockfiles:

```bash
dotnet sln Vinx.Erp.sln add src/Modules/Vinx.Erp.AI/Vinx.Erp.AI.csproj
dotnet sln Vinx.Erp.sln add tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj
dotnet restore Vinx.Erp.sln   # gera packages.lock.json dos projetos novos
```

- [ ] **Step 4: Run GREEN** — `dotnet test tests/public/Vinx.Erp.ArchitectureTests` → pass; `dotnet build Vinx.Erp.sln` → 0 erros.

- [ ] **Step 5: Formatar e commit**

```bash
dotnet format whitespace
git add -A
git commit -m "chore(ai): scaffold Vinx.Erp.AI module and architecture dependency rules"
```

---

### Task 2: Perfil de tarefa aprovado (server-side)

**Files:**
- Create: `src/Modules/Vinx.Erp.AI/AgentTaskProfile.cs` (record + enum `AgentJourneyScope`)
- Create: `src/Modules/Vinx.Erp.AI/AgentModelSelection.cs`
- Create: `src/Modules/Vinx.Erp.AI/AgentTaskProfileCatalog.cs`
- Create: `src/Modules/Vinx.Erp.AI/AgentCapabilities.cs`
- Test: `tests/public/Vinx.Erp.AI.UnitTests/AgentTaskProfileTests.cs`

**Interfaces:**
- Produces (usados pelas Tasks 3–4):

```csharp
public enum AgentJourneyScope { Company }

public sealed record AgentTaskProfile(
    string ProfileId,
    string Journey,
    AgentJourneyScope Scope,
    string RequiredCapabilityName,
    AgentModelSelection? ModelSelection,
    TimeSpan Timeout,
    int TurnLimit,
    int MaxTotalTokens,
    IReadOnlyList<string> AllowedTools)
{
    public static AgentTaskProfile CreateValidated(...mesmos parâmetros...); // lança ArgumentException com nome do parâmetro
    public void Validate(); // idem, para uso do catálogo
}

public sealed record AgentModelSelection(string ProviderId, string ModelId);

public sealed class AgentTaskProfileCatalog
{
    public const string ReadOnlySummarizeProfileId = "read_only_summarize_v1";
    public static AgentTaskProfile Get(string profileId); // KeyNotFoundException NÃO — ver Step 1: retorna null? NÃO: TryGet
    public static bool TryGet(string profileId, out AgentTaskProfile? profile);
}

public static class AgentCapabilities
{
    public const string Read = "ai.read";
    // UUIDv5 futuro (namespace https://vinx.erp/capabilities/): documentar no <summary>,
    // seed fica para a PR da API (#138). Sem seed, produção falha fechado (default deny).
}
```

- Catálogo: um único perfil aprovado `ReadOnlySummarizeProfileId` — journey `summarize`, escopo `Company`, capability `AgentCapabilities.Read`, `ModelSelection: null` (provider atribuído por #135; runner falha fechado com `provider_not_configured`), `Timeout: 30s`, `TurnLimit: 1`, `MaxTotalTokens: 8_000`, `AllowedTools: []`.

- [ ] **Step 1: RED — testes de validação do perfil** (`AgentTaskProfileTests.cs`, xUnit, sem fixtures):

Casos obrigatórios (um `[Fact]`/`[Theory]` cada):
1. `CreateValidated_accepts_read_only_summarize_v1_values` — constrói o perfil do catálogo sem exceção e asserts de valores (Timeout 30s, TurnLimit 1, AllowedTools vazio, ModelSelection null).
2. `Validate_rejects_empty_profile_id_journey_and_capability` — `""`/whitespace em ProfileId, Journey, RequiredCapabilityName → `ArgumentException`.
3. `Validate_rejects_non_positive_timeout` — `TimeSpan.Zero` e negativo → `ArgumentException`.
4. `Validate_rejects_turn_limit_above_one_or_below_one` — TurnLimit 0 e 2 → `ArgumentException` (sem loop nesta PR; comentário XML documenta que >1 exige revisão de política quando houver loop aprovado).
5. `Validate_rejects_non_positive_budget` — MaxTotalTokens 0/negativo → `ArgumentException`.
6. `Validate_rejects_company_scope_without_company_scoped_requirement` — Scope `Company` é o único valor válido no enum (assert estrutural: `Assert.True(Enum.IsDefined<AgentJourneyScope>(AgentJourneyScope.Company))` + nameof checks).
7. `Catalog_returns_approved_profile_and_rejects_unknown` — `TryGet("read_only_summarize_v1", out var p)` true + valores; `TryGet("inventado", out _)` false; `TryGet(" ", out _)` false.
8. `Catalog_profile_is_read_only` — AllowedTools vazio e TurnLimit 1 (trava regressão de mutação nesta PR).
9. `Client_cannot_inject_profile_values` — não há API/ctor que aceite timeout/modelo do cliente além de `CreateValidated` interno ao servidor; teste estrutural: todas as propriedades de `AgentTaskProfile` são getters init-only de record (assert via `typeof(...).GetProperties()` todos com `CanWrite == false` após construção — records positional têm init; usar `p.SetMethod?.ReturnParameter`… simplificação aceita: assert que o tipo é `sealed record` e imutável em prática via tentativa de `with` não alterando a instância original).

- [ ] **Step 2: Run RED** — `dotnet test tests/public/Vinx.Erp.AI.UnitTests` → falha de compilação (tipos não existem).

- [ ] **Step 3: GREEN — implementar** os quatro arquivos conforme Interfaces; `<summary>` português compacto; `AgentCapabilities.Read` documenta derivação UUIDv5 futura.

- [ ] **Step 4: Run GREEN** — suite AI pass; `dotnet build` limpo.

- [ ] **Step 5: Formatar e commit**

```bash
dotnet format whitespace
git add -A
git commit -m "feat(ai): server-side approved agent task profile catalog"
```

---

### Task 3: Contexto confiável e política de resolução

**Files:**
- Create: `src/Modules/Vinx.Erp.AI/AgentExecutionContext.cs`
- Create: `src/Modules/Vinx.Erp.AI/AgentPolicy.cs`
- Create: `src/Modules/Vinx.Erp.AI/AgentContextResolution.cs` (enum `AgentContextResolutionStatus` + record)
- Create: `src/Modules/Vinx.Erp.AI/IAgentContextResolver.cs`
- Create: `src/Modules/Vinx.Erp.AI/AgentContextResolver.cs` (implementação fina sobre `AuthorizedCompanySetResolver`)
- Test: `tests/public/Vinx.Erp.AI.UnitTests/AgentContextPolicyTests.cs`
- Test: `tests/public/Vinx.Erp.AI.UnitTests/AgentExecutionContextContractTests.cs`

**Interfaces:**
- Consumes: Foundation `ActorContext(Guid UserId, Guid CorrelationId, Guid? ExternalIdentityId, AuthenticatorAssuranceLevel AssuranceLevel)`; `AuthorizedCompanySetResolver.ResolveSnapshotAsync(actor, ct) → AuthorizationResult<ActorAuthorizationSnapshot>`; `ActorAuthorizationSnapshot(IReadOnlyList<MembershipCapabilitySummary> Companies)`; `MembershipCapabilitySummary(Guid MembershipId, Guid CompanyId, string CompanyName, IReadOnlyList<EffectiveCapability> Capabilities)`; `EffectiveCapability(string Name, IReadOnlySet<CapabilityScope> Scopes)`.
- Produces (usados pela Task 4):

```csharp
public sealed record AgentExecutionContext(
    Guid ActorId,
    Guid CorrelationId,
    Guid CompanyId,
    string CompanyName,
    Guid MembershipId,
    string ProfileId,
    IReadOnlySet<string> Capabilities);

public enum AgentContextResolutionStatus { Resolved, NeedsClarification, Denied, Unavailable }

public sealed record AgentContextResolution(
    AgentContextResolutionStatus Status,
    string ReasonCode,
    AgentExecutionContext? Context = null);

public interface IAgentContextResolver
{
    Task<AgentContextResolution> ResolveAsync(
        ActorContext actor,
        AgentTaskProfile profile,
        Guid? requestedCompanyId,
        CancellationToken cancellationToken);
}

public sealed class AgentContextResolver(AuthorizedCompanySetResolver resolver) : IAgentContextResolver;
```

`AgentPolicy` (classe estática, lógica pura — TODO o RED da Task 1 do handoff vive aqui):

```csharp
public static class AgentPolicy
{
    public static AgentContextResolution EvaluateContext(
        ActorAuthorizationSnapshot snapshot,
        AgentContextRequest request);   // record: ActorContext Actor, AgentTaskProfile Profile, Guid? RequestedCompanyId

    public static string? ValidateRequest(string message, AgentTaskProfile? profile);
    // null = válido; senão reason code ("message_empty" | "profile_unknown")
}
```

Regras de `EvaluateContext` (ORDEM FIXA — spec seção 4):
1. `RequestedCompanyId == null` e `Profile.Scope == Company` → `NeedsClarification("company_required")`.
2. `RequestedCompanyId` não presente nas memberships do snapshot → `Denied("company_not_authorized")` (fail-closed; nenhum dado da empresa solicitada é consultado — o snapshot já limita o universo).
3. Membership encontrada sem `RequiredCapabilityName` nas capabilities efetivas (escopo `CapabilityScope.Company`) → `Denied("capability_denied")`.
4. Sucesso → `Resolved` com `AgentExecutionContext` contendo SOMENTE a empresa autorizada (Beta nunca aparece) + capabilities NORMATIVAS (nomes) daquela membership + correlation do ator + profile id.

- [ ] **Step 1: RED — `AgentContextPolicyTests.cs`**

Casos obrigatórios (snapshots sintéticos — helper local `Snapshot(params MembershipCapabilitySummary[])`):
1. `Authenticated_actor_single_company_receives_that_company_context` — Alfa única + requested Alfa → Resolved; Context.CompanyId==alfa; Capabilities contém `ai.read`; CorrelationId==actor.CorrelationId; ProfileId correto.
2. `Actor_with_multiple_companies_receives_only_requested_company` — Alfa+Beta no snapshot, requested Alfa → Context contém SÓ Alfa (CompanyId, CompanyName, MembershipId da Alfa; nenhuma menção a Beta em nenhuma propriedade).
3. `Company_scoped_journey_without_company_needs_clarification` — requested null → `NeedsClarification`/`company_required`; Context null.
4. `Forged_company_fails_closed_without_authorized_data` — requested Gamma (não está no snapshot) → `Denied`/`company_not_authorized`; Context null; não há Gamma em lugar nenhum do resultado.
5. `Missing_capability_denies` — membership Alfa sem `ai.read` → `Denied`/`capability_denied`.
6. `Revoked_membership_not_reused` — resolver fake: 1ª chamada snapshot com Alfa; 2ª chamada snapshot SEM Alfa (revogada) → primeira Resolved, segunda `Denied("company_not_authorized")` (política consome snapshot fresco; sem cache).
7. `Correlation_id_flows_from_actor` — correlation distinto por ator aparece no Context (dois atores, dois correlations, sem mistura).
8. `ValidateRequest` — `"  "` → `message_empty`; profile null → `profile_unknown`; `"resuma X"` + perfil → null.

- [ ] **Step 2: RED — `AgentExecutionContextContractTests.cs`** (minimalidade do snapshot):
1. `Context_exposes_only_safe_properties` — reflection: nomes de propriedades de `AgentExecutionContext` EXATAMENTE `{ActorId, CorrelationId, CompanyId, CompanyName, MembershipId, ProfileId, Capabilities}` (allowlist; qualquer campo novo tipo prompt/JWT/e-mail quebra o teste).
2. `Context_default_tostring_contains_no_message_or_secret` — constrói context a partir de snapshot com mensagem sensível no teste; `record.ToString()` não contém a mensagem nem tokens de teste.
3. `Capability_names_are_minimal_strings` — Capabilities é `IReadOnlySet<string>` (nomes, sem scopes/ids extras).

- [ ] **Step 3: Run RED** — compilação falha (tipos não existem).

- [ ] **Step 4: GREEN — implementar** os cinco arquivos conforme Interfaces e regras. `AgentContextResolver.ResolveAsync`: `resolver.ResolveSnapshotAsync(actor, ct)`; `IsSuccess == false` → `Unavailable("context_unavailable")`; senão `AgentPolicy.EvaluateContext(new(snapshot…))`. XML summary documenta que RLS/audit pertencem à Foundation.

- [ ] **Step 5: Run GREEN** — suite AI pass.

- [ ] **Step 6: Formatar e commit**

```bash
dotnet format whitespace
git add -A
git commit -m "feat(ai): trusted agent context resolution with fail-closed policy"
```

---

### Task 4: Runner, outcome normalizado e telemetria

**Files:**
- Create: `src/Modules/Vinx.Erp.AI/AgentOutcome.cs` (enum `AgentOutcomeStatus` + `AgentOutcomeReason` + record `AgentOutcome`)
- Create: `src/Modules/Vinx.Erp.AI/IAgentModel.cs`
- Create: `src/Modules/Vinx.Erp.AI/AgentRunner.cs`
- Create: `src/Modules/Vinx.Erp.AI/AgentTelemetry.cs`
- Test: `tests/public/Vinx.Erp.AI.UnitTests/TestAgentModel.cs` (fake)
- Test: `tests/public/Vinx.Erp.AI.UnitTests/SequenceContextResolver.cs` (fake IAgentContextResolver programável por chamada)
- Test: `tests/public/Vinx.Erp.AI.UnitTests/AgentRunnerTests.cs`
- Test: `tests/public/Vinx.Erp.AI.UnitTests/AgentRunnerOutcomeTests.cs`
- Test: `tests/public/Vinx.Erp.AI.UnitTests/AgentRunnerIsolationTests.cs`

**Interfaces:**
- Consumes: Tasks 2–3 (`AgentTaskProfile`, `IAgentContextResolver`, `AgentContextResolution`, `AgentExecutionContext`, `AgentPolicy.ValidateRequest`); Foundation `ActorContext`.
- Produces:

```csharp
public enum AgentOutcomeStatus { Completed, NeedsClarification, RequiresConfirmation, Denied, Invalid, Unavailable, TimedOut }

public static class AgentOutcomeReason
{
    public const string Completed = "completed";
    public const string MessageEmpty = "message_empty";
    public const string ProfileUnknown = "profile_unknown";
    public const string CompanyRequired = "company_required";
    public const string CompanyNotAuthorized = "company_not_authorized";
    public const string CapabilityDenied = "capability_denied";
    public const string ContextUnavailable = "context_unavailable";
    public const string ProviderNotConfigured = "provider_not_configured";
    public const string ModelUnavailable = "model_unavailable";
    public const string ModelTimeout = "model_timeout";
    public const string ToolProposalRequiresConfirmation = "tool_proposal_requires_confirmation";
}

public sealed record AgentOutcome(
    AgentOutcomeStatus Status,
    string ReasonCode,
    string? Text,
    string? ProposedActionName,
    Guid CorrelationId,
    TimeSpan Elapsed);

public interface IAgentModel
{
    // Fronteira Microsoft.Extensions.AI: normalização de turns/outcomes somente.
    Task<ChatResponse> GetResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        AgentModelSelection selection,
        CancellationToken cancellationToken);
}

public sealed class AgentRunner(IAgentContextResolver contextResolver, IAgentModel? model, ILogger<AgentRunner> logger)
{
    public async Task<AgentOutcome> RunAsync(
        ActorContext actor,
        string profileId,
        string message,
        Guid? requestedCompanyId,
        CancellationToken cancellationToken);
}
```

`AgentRunner.RunAsync` (ORDEM FIXA, uma chamada de modelo no máximo):
1. `Stopwatch` + `AgentPolicy.ValidateRequest(message, catalog.TryGet(...))` → `Invalid` (`message_empty`/`profile_unknown`) SEM resolver e SEM modelo.
2. `contextResolver.ResolveAsync(...)` → mapeia `NeedsClarification/Denied/Unavailable` para outcome com reason correspondente; SEM modelo.
3. `profile.ModelSelection == null` → `Unavailable("provider_not_configured")`; SEM modelo (fail-closed até #135).
4. Normalização de turn: `new List<ChatMessage> { new(ChatRole.User, message) }` — UMA chamada `model.GetResponseAsync(messages, selection, linkedCts.Token)` com `CancellationTokenSource.CreateLinkedTokenSource(ct)` + `CancelAfter(profile.Timeout)`.
5. Normalização de outcome (tipos `Microsoft.Extensions.AI.Abstractions`):
   - `OperationCanceledException` quando `ct.IsCancellationRequested` → rethrow; senão `TimedOut("model_timeout")`.
   - resposta com `FunctionCallContent` em `ChatMessage.Contents` → `RequiresConfirmation("tool_proposal_requires_confirmation")` + `ProposedActionName = call.Name` (descriptor seguro); NUNCA executa.
   - texto → `Completed("completed")` + `Text`.
   - qualquer outra exceção → `Unavailable("model_unavailable")`.
6. Correlation: outcome sempre com `actor.CorrelationId`; log único estruturado ao fim (`Information` para Completed, `Warning` para denied/invalid/needs_clarification — ver regra AGENTS: validação comum esperada NÃO usa Warning… validação esperada usa `Information`; `Warning` fica para denied suspeito? Ruling: `Information` para Completed/NeedsClarification/Invalid; `Warning` para Denied; `Error` para Unavailable/TimedOut com exceção preservada) com template constante `"Agent execution {Operation} {Module} {Result} {Reason} {ElapsedMs}"`, correlação via `logger.BeginScope` com `CorrelationId`.
7. `AgentTelemetry`: Meter `"Vinx.Erp.AI"`, counter `ai.agent.executions` com tags `result` (status snake_case) e `reason` — SEM prompt/resposta/PII em tag (tags de baixa cardinalidade: status enums).

- [ ] **Step 1: RED — fakes**

`TestAgentModel`: `List<ChatResponse> Responses` em sequência + `int CallCount` + registro dos `messages`/`selection` recebidos; construtores para texto puro, tool call (`ChatMessage(AuthorRole.Assistant, [new FunctionCallContent("tool_x", "id", arguments)])` — usar API real do pacote: `FunctionCallContent.CallId/Name`), exceção, atraso + cancelamento. `SequenceContextResolver`: fila de `AgentContextResolution` + contagem de chamadas.

- [ ] **Step 2: RED — `AgentRunnerTests.cs` (validação/erro/acesso/estado sem provider)**

Casos obrigatórios (todos assertam `model.CallCount == 0` quando aplicável):
1. `Empty_message_returns_invalid_without_resolution_or_model` — resolver e modelo JAMAIS chamados (`contextResolver.CallCount == 0`).
2. `Unknown_profile_returns_invalid_without_model`.
3. `Missing_company_returns_needs_clarification_without_model` (resolver devolve NeedsClarification).
4. `Denied_company_returns_denied_without_model`.
5. `Revoked_membership_between_runs_denies_second_run` — resolver sequência [Resolved(Alfa), Denied]; 1ª Completed (com modelo texto), 2ª Denied SEM modelo na segunda execução.
6. `Unavailable_context_returns_unavailable_without_model`.
7. `Provider_not_configured_returns_unavailable_without_model` — perfil do catálogo (ModelSelection null) + resolver Resolved → `Unavailable("provider_not_configured")`, `model.CallCount == 0`.
8. `Success_flow_calls_model_once_with_user_message` — CallCount==1; mensagem recebida é o texto do usuário; `Completed` + Text; CorrelationId do ator; Elapsed >= 0.
9. `No_model_call_happens_before_resolution` — coberto por 1–7 (asserção transversal documentada no teste 8 via ordem registrada).

- [ ] **Step 3: RED — `AgentRunnerOutcomeTests.cs` (normalização)**

1. `Tool_proposal_requires_confirmation_and_never_executes` — resposta com FunctionCallContent → `RequiresConfirmation` + `ProposedActionName == "tool_x"` + Text null; `Completed != status`; nenhum mecanismo de execução existe (assert estrutural: `IAgentModel` não recebe ferramentas; `AllowedTools` vazio no perfil do catálogo já garantido na Task 2).
2. `Model_exception_returns_unavailable` — fake lança → `Unavailable("model_unavailable")`.
3. `Model_timeout_returns_timed_out` — fake aguarda > timeout (perfil teste com `Timeout: 50ms`) → `TimedOut("model_timeout")`.
4. `External_cancellation_propagates` — ct externo cancelado antes → `OperationCanceledException` propagada (não vira outcome).
5. `Outcome_carries_only_safe_fields` — reflection allowlist: propriedades de `AgentOutcome` exatamente `{Status, ReasonCode, Text, ProposedActionName, CorrelationId, Elapsed}`.

- [ ] **Step 4: RED — `AgentRunnerIsolationTests.cs` (concorrência)**

1. `Concurrent_actors_do_not_mix_context_or_outcome` — 2 atores/2 empresas/2 correlations; resolvers e modelos por-ator; `Task.WhenAll`; outcomes casam 1:1 com seus atores (Status/Text/CorrelationId); CallCounts 1 cada.
2. `Concurrent_same_actor_different_companies` — mesmo ator, requested Alfa e Beta (ambos no snapshot), 2 runs paralelos → cada outcome reflete a própria empresa requisitada (Text do fake distinto por selection/message).

- [ ] **Step 5: Run RED** — falha de compilação.

- [ ] **Step 6: GREEN — implementar** `AgentOutcome.cs`, `IAgentModel.cs`, `AgentRunner.cs`, `AgentTelemetry.cs` conforme Interfaces. Runner é stateless (tudo local por chamada). `ILogger<AgentRunner>` injetado (Microsoft.Extensions.Logging.Abstractions via Foundation transitivo — se não disponível, adicionar package `Microsoft.Extensions.Logging.Abstractions` 10.x em props com justificativa; verificar antes se já é transitivo de MEAI.Abstractions).

- [ ] **Step 7: Run GREEN** — suite AI completa pass; `dotnet build` limpo; `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests` segue verde.

- [ ] **Step 8: Formatar e commit**

```bash
dotnet format whitespace
git add -A
git commit -m "feat(ai): agent runner with policy-first execution and normalized outcomes"
```

---

### Task 5: Documentação de módulo e casos de teste

**Files:**
- Modify: `docs/modules/ai.md` (de "adiado" para o contrato real entregue)
- Create: `docs/verification/issue-127-132-agent-context-test-cases.md`

**Interfaces:**
- Consumes: tudo das Tasks 1–4 (nomes/reasons finais devem bater).
- Constraint: `ModuleContractDocumentationTests` exige headings `{## Ownership, ## Company scope, ## Contract version, ## Deletion strategy, ## Migration notes}` em `docs/modules/ai.md` — manter todos; Contract version sobe de `2.0` para `3.0`.

- [ ] **Step 1: Escrever `docs/modules/ai.md`** (português, compacto): Estado = base mínima hospedada entregue (módulo + perfil aprovado + contexto confiável; sem provider/API/UI); Ownership (orquestração de inferência no AI; identidade/autorização/RLS/auditoria na Foundation); Company scope (política fail-closed: `company_required`/`company_not_authorized`/`capability_denied`; mensagem/canal nunca escolhem empresa); Contract version `3.0`; Deletion strategy (módulo sem dados próprios); Migration notes (capability `ai.read` constante no módulo; seed na DB fica para #138 — sem seed, falha fechado; nenhuma migração nesta PR).

- [ ] **Step 2: Escrever `docs/verification/issue-127-132-agent-context-test-cases.md`** — matriz BDD 5 classes (sucesso/validação/acesso/estado/concorrência) mapeando CADA caso dos testes das Tasks 2–4 para cenário/classe/comando; formato espelha `docs/verification/*.md` existentes (ver `transactional-owner-outbox-test-cases.md`).

- [ ] **Step 3: Verificações finais**

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests
dotnet test tests/public/Vinx.Erp.ArchitectureTests
dotnet format whitespace --verify-no-changes
git diff --check
```

Todas verdes/limpas.

- [ ] **Step 4: Commit**

```bash
git add docs/
git commit -m "docs(ai): module contract and agent context test cases"
```

---

## Verificação final (freeze — executar serializado, sem paralelo)

1. `./scripts/verify-public.sh` EXIT:0 no HEAD exato (inclui npm/web checks, restore locked-mode, format, DB fixture e TODOS os testes públicos).
2. Revisão final de branch inteira (subagent code-reviewer): contexto, ordem da policy, redaction, ausência de execução de tools, ausência de provider real.
3. `git diff --check` limpo.
4. Evidência no PR: veredito, BDD, gate, heads.
