# Agent Harness Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Entregar o núcleo supervisionado e independente de provedor do Agent Harness, pronto para adaptadores futuros sem executar tool calls automaticamente.

**Architecture:** Criar o módulo `Vinx.Erp.AI`, dependente somente de Foundation. O runner aceita contexto confiável e request não confiável, recebe decisões tipadas de `IAgentModel`, aplica policy antes de validação semântica e só invoca leitura permitida. Adaptadores de `IChatClient`, MCP e canais ficam fora desta fatia.

**Tech Stack:** .NET 10, ASP.NET Core shared framework, System.Text.Json, xUnit, Microsoft.Extensions.AI apenas nas futuras PRs de adaptador, Foundation e testes públicos.

## Global Constraints

- Esta fatia é a replanejada issue #127; nenhuma branch/PR de harness em rascunho é mesclada ou apagada por este plano.
- `src/Modules/AI/` segue a estrutura modular aprovada em `docs/superpowers/specs/2026-08-08-vinx-erp-architecture-design.md`; `docs/modules/ai.md` registra essa ownership quando o módulo existir.
- Não adicionar pacote nesta fatia. `Microsoft.Extensions.AI` entra somente no primeiro adaptador real de #135, com uso de produção e lockfile atualizado.
- `Vinx.Erp.AI` referencia somente `Vinx.Erp.Foundation`; nunca `ApiHost`, `McpHost`, `WorkerHost`, React, WhatsApp ou provider SDK.
- Contexto confiável contém ator, correlação, empresa, capabilities e perfil; mensagem, OCR, anexo, argumento de tool e resposta de modelo são não confiáveis.
- Ordem obrigatória: allowlist do perfil, empresa explícita, capability/membership, risco/confirmação, validação semântica, serviço de aplicação.
- Nenhuma biblioteca, modelo ou adapter pode executar uma ferramenta automaticamente. Não usar `FunctionInvokingChatClient` ou `UseFunctionInvocation()`.
- Nunca registrar prompt, resposta, conteúdo OCR, anexo, argumento bruto, token, segredo, PII ou conteúdo médico.
- Rodar somente `tests/public`; nunca listar, abrir ou executar `quality/private`.
- Cada PR mantém no máximo 25 arquivos e 800 linhas manuais, salvo autorização explícita posterior. Migrações e lockfiles não justificam misturar subsistemas.
- Qwen 3.8 Max revisa este plano agora e cada diff de código antes de push. A revisão é read-only, advisory e não substitui testes, revisão humana ou gates públicos.

---

## Scope split and delivery map

Esta implementação cobre somente #127. Os demais blocos de V1 são independentes e recebem plano próprio, sobre o commit integrado que os desbloquear. Não iniciar seus códigos como “continuação” desta stack.

| Fase | Issue(s) | Dependências de entrada | Paralelismo permitido | Resultado |
| --- | --- | --- | --- | --- |
| Identidade | #81, #18, #19, #20 | #21 para #81; #19 antes de #20 | #127 pode avançar em paralelo | identidade, capability e empresa confiáveis |
| Core | #127-A até #127-D | nenhuma | paralelo com identidade | contracts, policy, runner e fronteira AI |
| Contexto | #132 | #18, #19, #20, #127 | não | resolvedor de contexto/profile confiável |
| Providers | #135-A OpenAI-compatible, #135-B Anthropic | #132 | A e B em paralelo | adapters reais sem autoexecução |
| Segurança de saída | #136-A outcome/abstention, #136-B fontes/redaction | #132 e providers | somente após contratos de provider | resposta estruturada e segura |
| Aprovação | #126 | #19, #20; integração com #127 | paralelo com providers após identidade | approval durável e idempotente |
| MCP remoto | #133-A OAuth, #133-B Streamable HTTP, #125, #139 | #132; #126 antes do catálogo de escrita | OAuth e approval podem avançar em paralelo | clientes Claude/GPT interoperáveis |
| Contexto rico | #137, #134 | #137: #132/#135; #134: #52/#104/#127 | #137 e #134 em paralelo quando bases prontas | DeepSeek controlado e OCR normalizado |
| Canais | #138, #53, #140 | #138: #126/#132/#135/#136; #140 também #28/#48/#52/#134/#137 | UI após #138; WhatsApp após todas dependências | UI e WhatsApp V1 supervisionados |
| Primeiro domínio | #55 | domínio Paguai, #126, #139 | depois de catálogo/aprovação | leitura Paguai e rascunho confirmado |

V2 só considera workflow longo, memória governada, subagentes ou nova avaliação de Microsoft Agent Framework depois de métricas e avaliações de V1. V2 não remove empresa explícita, capability, confirmação, auditoria ou idempotência.

## Planned file structure for #127

| Arquivo | Responsabilidade |
| --- | --- |
| `src/Modules/AI/Vinx.Erp.AI/Vinx.Erp.AI.csproj` | Projeto de módulo AI; única referência de projeto é Foundation. |
| `src/Modules/AI/Vinx.Erp.AI/AgentHarness/AgentContracts.cs` | Contexto confiável, request não confiável, perfil, outcome e canal. |
| `src/Modules/AI/Vinx.Erp.AI/Models/AgentModelContracts.cs` | Contrato tipado e neutro para decisão de modelo; futuro adapter `IChatClient` o implementa. |
| `src/Modules/AI/Vinx.Erp.AI/Tools/AgentToolContracts.cs` | Descritor MCP-oriented, proposta sanitizada, validação, invocação e resultado. |
| `src/Modules/AI/Vinx.Erp.AI/Tools/AgentToolRegistry.cs` | Catálogo imutável, sem descoberta global automática. |
| `src/Modules/AI/Vinx.Erp.AI/Tools/AgentRunPolicy.cs` | Allowlist, empresa, capability e risco antes de tool validation. |
| `src/Modules/AI/Vinx.Erp.AI/AgentHarness/AgentRunObservation.cs` | Evento seguro e observer sem conteúdo sensível. |
| `src/Modules/AI/Vinx.Erp.AI/AgentHarness/AgentHarnessRunner.cs` | Loop limitado, cancelável, manual e determinístico. |
| `tests/public/Vinx.Erp.AI.UnitTests/*.cs` | Testes de contracts, policy, registry e runner; doubles somente de teste. |
| `tests/public/Vinx.Erp.ArchitectureTests/AiDependencyTests.cs` | Garante dependências do módulo e proíbe hosts. |
| `docs/modules/ai.md` | Ownership, escopo de empresa, versão, deleção e notas de migração do módulo. |
| `Vinx.Erp.sln` | Registra projeto AI e projeto de testes públicos. |

## PR boundaries

| PR | Branch | Base | Conteúdo | Estimativa manual |
| --- | --- | --- | --- | --- |
| #127-A contracts | `codex/agent-v1-core-contracts` | `main` | módulo AI, contracts e testes de invariantes | 7 arquivos, até 330 linhas |
| #127-B tools/policy | `codex/agent-v1-core-tools` | #127-A | model/tool contracts, registry, sanitizer, policy e testes de não-invocação | 7 arquivos, até 480 linhas |
| #127-C runner | `codex/agent-v1-core-runner` | #127-B | observer, runner, doubles e testes de timeout/cancelamento/concorrência | 5 arquivos, até 560 linhas |
| #127-D docs/boundary | `codex/agent-v1-core-boundary` | #127-C | module doc, architecture test, revisão final e atualização de PR | 4 arquivos, até 220 linhas |

Recalcular arquivos e linhas antes de iniciar cada PR. Se a estimativa real ultrapassar estes limites, dividir a PR antes de código.

### Task 1: Create AI module and trusted/untrusted contracts (#127-A)

**Files:**
- Create: `src/Modules/AI/Vinx.Erp.AI/Vinx.Erp.AI.csproj`
- Create: `src/Modules/AI/Vinx.Erp.AI/packages.lock.json`
- Create: `src/Modules/AI/Vinx.Erp.AI/AgentHarness/AgentContracts.cs`
- Create: `tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj`
- Create: `tests/public/Vinx.Erp.AI.UnitTests/packages.lock.json`
- Create: `tests/public/Vinx.Erp.AI.UnitTests/AgentContractsTests.cs`
- Modify: `Vinx.Erp.sln`

**Interfaces:**
- Consumes: `Vinx.Erp.Foundation.Identity.ActorContext`.
- Produces: `AgentExecutionContext`, `AgentRunRequest`, `AgentContextItem`, `AgentTaskProfile`, `AgentRunOutcome`, `AgentRunOutcomeKind` and `AgentChannel`.
- Invariant: nenhum contrato desta PR recebe provider, endpoint, modelo, capability, empresa ou correlation ID de dados não confiáveis.

- [ ] **Step 1: Write failing contract tests**

Create `AgentContractsTests.cs` with invariant tests before creating production types.

```csharp
[Fact]
public void Execution_context_copies_capabilities_and_rejects_empty_actor()
{
    var capabilities = new HashSet<string>(["paguai.read"], StringComparer.Ordinal);
    var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());

    var context = new AgentExecutionContext(
        actor, Guid.NewGuid(), AgentChannel.Web, capabilities, CreateProfile());

    capabilities.Clear();

    Assert.Contains("paguai.read", context.EffectiveCapabilities);
    Assert.Throws<ArgumentOutOfRangeException>(() => new AgentExecutionContext(
        new ActorContext(Guid.Empty, Guid.NewGuid()),
        Guid.NewGuid(), AgentChannel.Web, new HashSet<string>(), CreateProfile()));
}

```

- [ ] **Step 2: Run focused test and observe compilation failure**

Run:

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore
```

Expected: project path is absent.

- [ ] **Step 3: Create project and minimal contracts**

Create the AI project with only Foundation as project reference; copy the existing xUnit test-project package pattern. Add both projects to `Vinx.Erp.sln`. Implement immutable contracts with validation.

```csharp
public sealed record AgentExecutionContext(
    ActorContext Actor,
    Guid? CompanyId,
    AgentChannel Channel,
    IReadOnlySet<string> EffectiveCapabilities,
    AgentTaskProfile Profile);

public sealed record AgentRunRequest(
    string UserMessage,
    IReadOnlyList<AgentContextItem> ExternalContext);

public sealed record AgentRunOutcome(
    AgentRunOutcomeKind Kind,
    string SafeCode,
    string? AssistantMessage,
    string? ToolName);
```

The constructors must reject blank messages, blank capability/tool names, empty actor/correlation IDs, empty non-null company IDs, unsupported channel/outcome values, invalid confidence and non-positive timeout/turn limits. Copy collections with ordinal comparers.

- [ ] **Step 4: Restore and run contract tests**

Run:

```bash
dotnet restore
dotnet restore --locked-mode
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter FullyQualifiedName~AgentContractsTests
```

Expected: all `AgentContractsTests` pass. Inspect generated lockfiles and include only those under the two new project directories.

- [ ] **Step 5: Ask Qwen for read-only diff review, then commit contract slice**

Run:

```bash
git add Vinx.Erp.sln src/Modules/AI/Vinx.Erp.AI tests/public/Vinx.Erp.AI.UnitTests
opencode run --dir "$PWD" --model opencode-go/qwen3.8-max --file docs/superpowers/specs/2026-08-11-agent-framework-boundaries-design.md --file docs/superpowers/plans/2026-08-11-agent-harness-core-framework.md "Revisão read-only de #127-A. Inspecione git diff --cached. Procure contexto confiável vazando para request não confiável, mutação indevida, contratos mutáveis ou dependência fora de Foundation. Não altere arquivos, não faça git write e não acesse quality/private. Liste somente achados P0/P1 com arquivo, linha, motivo e correção."
git commit -m "feat(ai): add harness contracts"
```

### Task 2: Add job-tool contracts, immutable registry and server policy (#127-B)

**Files:**
- Create: `src/Modules/AI/Vinx.Erp.AI/Models/AgentModelContracts.cs`
- Create: `src/Modules/AI/Vinx.Erp.AI/Tools/AgentToolContracts.cs`
- Create: `src/Modules/AI/Vinx.Erp.AI/Tools/AgentToolRegistry.cs`
- Create: `src/Modules/AI/Vinx.Erp.AI/Tools/AgentRunPolicy.cs`
- Create: `tests/public/Vinx.Erp.AI.UnitTests/AgentToolRegistryTests.cs`
- Create: `tests/public/Vinx.Erp.AI.UnitTests/AgentRunPolicyTests.cs`
- Modify: `tests/public/Vinx.Erp.AI.UnitTests/AgentContractsTests.cs`

**Interfaces:**
- Consumes: contracts from Task 1.
- Produces: `IAgentModel`, `AgentModelRequest`, `AgentModelTurn`, `IAgentTool`, `AgentToolDescriptor`, `AgentToolCall`, `AgentToolValidationResult`, `AgentToolResult`, `AgentToolInvocation`, `AgentToolRegistry`, `AgentRunPolicy` and `AgentPolicyDecision`.
- Policy result: `tool_not_allowed`, `company_context_required`, `capability_denied`, `confirmation_required` or `tool_allowed`.
- Model invariant: `AgentModelRequest` exposes only request, allowed descriptors and prior results; it has no actor, company, capability, correlation or profile.
- Exact model contracts: `AgentModelRequest(AgentRunRequest Request, IReadOnlyList<AgentToolDescriptor> AllowedTools, IReadOnlyList<AgentToolResult> PriorToolResults)` and `AgentModelTurn(string? AssistantMessage, AgentToolCall? ToolCall)`; a turn has exactly one of final message or tool call.
- Exact invocation contract: `AgentToolInvocation(Guid RunId, AgentExecutionContext ExecutionContext, AgentToolCall Call)`; its constructor accepts only a sanitized call and preserves the trusted execution context unchanged.

- [ ] **Step 1: Write failing policy and registry tests**

Create test cases for duplicate tool name/version, immutable descriptor snapshots and exact policy precedence.

```csharp
[Theory]
[InlineData(false, true, true, AgentToolRisk.ReadOnly, AgentRunOutcomeKind.Denied, "tool_not_allowed")]
[InlineData(true, false, true, AgentToolRisk.ReadOnly, AgentRunOutcomeKind.NeedsClarification, "company_context_required")]
[InlineData(true, true, false, AgentToolRisk.ReadOnly, AgentRunOutcomeKind.Denied, "capability_denied")]
[InlineData(true, true, true, AgentToolRisk.InternalWrite, AgentRunOutcomeKind.RequiresConfirmation, "confirmation_required")]
public void Policy_uses_fixed_precedence(
    bool allowed, bool company, bool capability, AgentToolRisk risk,
    AgentRunOutcomeKind expectedKind, string expectedCode)
{
    var decision = new AgentRunPolicy().Evaluate(
        CreateContext(allowed, company, capability),
        CreateDescriptor(risk));

    Assert.Equal(expectedKind, decision.OutcomeKind);
    Assert.Equal(expectedCode, decision.SafeCode);
}
```

Add tests that submit top-level and nested `empresa_id`, `empresaId`, `company_id` and `companyId` properties and prove `AgentToolCall` removes them recursively at every JSON depth before an in-process tool can read arguments.

Add a reflection test proving `AgentModelRequest` lacks `Actor`, `CompanyId`, `EffectiveCapabilities`, `CorrelationId` and `Profile`.

- [ ] **Step 2: Run focused tests and observe missing types**

Run:

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter "FullyQualifiedName~AgentRunPolicyTests|FullyQualifiedName~AgentToolRegistryTests"
```

Expected: compilation failure for missing model/tool contracts, registry and policy.

- [ ] **Step 3: Implement descriptors, sanitizer, registry and policy**

Use `JsonElement.Clone()` for schema/payload ownership. Descriptor includes stable name/version, input/output schemas, required capability, risk, explicit-company requirement, idempotency requirement and MCP hints. `AgentToolInvocation` receives its actor, correlation and optional company only through `AgentExecutionContext`; it receives the recursively sanitized `AgentToolCall`, never a company extracted from model JSON. Registry rejects duplicate name/version registrations and exposes immutable descriptor snapshots.

```csharp
public AgentPolicyDecision Evaluate(
    AgentExecutionContext context,
    AgentToolDescriptor descriptor)
{
    if (!context.Profile.AllowedTools.Contains(descriptor.Name))
        return new AgentPolicyDecision(AgentRunOutcomeKind.Denied, "tool_not_allowed");
    if (descriptor.RequiresExplicitCompany && context.CompanyId is null)
        return new AgentPolicyDecision(
            AgentRunOutcomeKind.NeedsClarification, "company_context_required");
    if (!context.EffectiveCapabilities.Contains(descriptor.RequiredCapability))
        return new AgentPolicyDecision(AgentRunOutcomeKind.Denied, "capability_denied");
    if (descriptor.Risk != AgentToolRisk.ReadOnly)
        return new AgentPolicyDecision(
            AgentRunOutcomeKind.RequiresConfirmation, "confirmation_required");

    return new AgentPolicyDecision(AgentRunOutcomeKind.Result, "tool_allowed");
}
```

Implement the model boundary in the same slice:

```csharp
public interface IAgentModel
{
    Task<AgentModelTurn> CompleteAsync(
        AgentModelRequest request,
        CancellationToken cancellationToken);
}
```

Do not call `IAgentTool.Validate` or `IAgentTool.InvokeAsync` in this task. No schema library and no generic CRUD tool are introduced.

- [ ] **Step 4: Run focused tests and public formatting**

Run:

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter "FullyQualifiedName~AgentRunPolicyTests|FullyQualifiedName~AgentToolRegistryTests|FullyQualifiedName~AgentContractsTests"
dotnet format whitespace --verify-no-changes
```

Expected: focused AI tests pass and formatting reports no differences.

- [ ] **Step 5: Ask Qwen for read-only diff review, then commit tool-policy slice**

Run:

```bash
git add src/Modules/AI/Vinx.Erp.AI tests/public/Vinx.Erp.AI.UnitTests
opencode run --dir "$PWD" --model opencode-go/qwen3.8-max --file docs/superpowers/specs/2026-08-11-agent-framework-boundaries-design.md --file docs/superpowers/plans/2026-08-11-agent-harness-core-framework.md "Revisão read-only de #127-B. Inspecione git diff --cached. Procure falha na ordem allowlist/empresa/capability/risco, schema/payload mutável, alias de empresa chegando à tool, catálogo mutável ou caminho de autoexecução. Não altere arquivos, não faça git write e não acesse quality/private. Liste somente achados P0/P1 com arquivo, linha, motivo e correção."
git commit -m "feat(ai): add tool policy"
```

### Task 3: Add deterministic runner, safe events and cancellation behavior (#127-C)

**Files:**
- Create: `src/Modules/AI/Vinx.Erp.AI/AgentHarness/AgentRunObservation.cs`
- Create: `src/Modules/AI/Vinx.Erp.AI/AgentHarness/AgentHarnessRunner.cs`
- Create: `tests/public/Vinx.Erp.AI.UnitTests/Fixtures/AgentHarnessDoubles.cs`
- Create: `tests/public/Vinx.Erp.AI.UnitTests/AgentHarnessRunnerTests.cs`
- Modify: `tests/public/Vinx.Erp.AI.UnitTests/AgentToolRegistryTests.cs`

**Interfaces:**
- Consumes: contracts, registry and policy from Tasks 1–2.
- Produces: `AgentHarnessRunner.RunAsync(AgentExecutionContext, AgentRunRequest, CancellationToken)`, `IAgentRunObserver`, `AgentRunEvent` and `AgentRunPhase`.
- Invocation rule: only an allowed `ReadOnly` tool may reach `Validate` then `InvokeAsync`; write/denied/clarification tool calls never reach either method.
- Descriptor rule: before every model call, the runner passes only `toolRegistry.Descriptors ∩ context.Profile.AllowedTools` in `AgentModelRequest.AllowedTools`.
- Trusted invocation rule: `new AgentToolInvocation(runId, context, sanitizedCall)` is the only construction path; actor, company and correlation come from `context`, not model JSON.
- Concurrency rule: runner state is local to one `RunAsync` call; every `IAgentRunObserver` implementation supplied to a shared runner must be thread-safe.
- `AgentRunEvent` is operational metadata, not durable audit. Application services and the later Foundation approval flow own durable business/security audit.

- [ ] **Step 1: Write failing runner tests**

Create deterministic model/tool/observer doubles and test final reply, allowed read, unknown tool, denied write, missing company, validation failure, failed tool result without automatic retry, turn limit, timeout, caller cancellation and concurrent isolation.

```csharp
[Fact]
public async Task Run_requires_confirmation_before_validation_or_invocation()
{
    var tool = new RecordingAgentTool("payments.draft_request", AgentToolRisk.InternalWrite);
    var harness = CreateHarness(
        ScriptedAgentModel.Tool("payments.draft_request", "{}"),
        tool);

    var outcome = await harness.RunAsync(CreateWriteContext(), CreateRequest(), CancellationToken.None);

    Assert.Equal(AgentRunOutcomeKind.RequiresConfirmation, outcome.Kind);
    Assert.Empty(tool.Validations);
    Assert.Empty(tool.Invocations);
}

[Fact]
public async Task Run_keeps_parallel_actor_company_and_correlation_isolated()
{
    var outcomes = await Task.WhenAll(
        harness.RunAsync(CreateContext(actorA, alfa, correlationA), CreateRequest(), CancellationToken.None),
        harness.RunAsync(CreateContext(actorB, beta, correlationB), CreateRequest(), CancellationToken.None));

    Assert.All(outcomes, outcome => Assert.Equal(AgentRunOutcomeKind.Result, outcome.Kind));
    Assert.All(observer.Events, runEvent =>
        Assert.Contains(runEvent.CompanyId, new Guid?[] { alfa, beta }));
}

[Fact]
public async Task Run_exposes_only_profile_allowed_descriptors_to_model()
{
    var model = new RecordingAgentModel(AgentModelTurn.Final("safe"));
    var harness = CreateHarness(
        model,
        new RecordingAgentTool("payments.read"),
        new RecordingAgentTool("payments.admin"));

    await harness.RunAsync(
        CreateContext(allowedTools: ["payments.read"]),
        CreateRequest(),
        CancellationToken.None);

    Assert.Equal(["payments.read"], model.Requests.Single().AllowedTools.Select(tool => tool.Name));
}

[Fact]
public async Task Run_builds_tool_invocation_from_trusted_context()
{
    var tool = new RecordingAgentTool("payments.read");
    var alpha = Guid.NewGuid();
    var harness = CreateHarness(
        ScriptedAgentModel.Tool("payments.read", """{"company_id":"forged"}"""),
        tool);

    await harness.RunAsync(CreateContext(companyId: alpha), CreateRequest(), CancellationToken.None);

    Assert.Equal(alpha, tool.Invocations.Single().ExecutionContext.CompanyId);
    Assert.DoesNotContain("company_id", tool.Invocations.Single().Call.Arguments.GetRawText());
}
```

Serialize observer events and assert that known user-message, model-answer, OCR and raw-argument strings are absent.

- [ ] **Step 2: Run focused runner tests and observe failure**

Run:

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter FullyQualifiedName~AgentHarnessRunnerTests
```

Expected: compilation failure because runner and observer contracts are absent.

- [ ] **Step 3: Implement manual loop with bounded execution**

Create and dispose linked cancellation sources for profile timeout and caller cancellation. Before every model call, derive the immutable allowed descriptor snapshot from the registry/profile intersection. For every proposal resolve the registry, evaluate policy, reject non-allowed outcomes without validation, check turn limit, validate only allowed reads, construct an invocation from trusted context plus sanitized call, invoke the tool, append typed result and request another model turn. A final model message completes the run.

```csharp
var allowedDescriptors = toolRegistry.Descriptors
    .Where(descriptor => context.Profile.AllowedTools.Contains(descriptor.Name))
    .ToArray();
using var timeoutSource = new CancellationTokenSource(context.Profile.Timeout);
using var executionSource = CancellationTokenSource.CreateLinkedTokenSource(
    cancellationToken,
    timeoutSource.Token);

var turn = await model.CompleteAsync(
    new AgentModelRequest(request, allowedDescriptors, priorResults),
    executionSource.Token);

var decision = policy.Evaluate(context, registration.Descriptor);
if (decision.OutcomeKind != AgentRunOutcomeKind.Result)
{
    return Complete(new AgentRunOutcome(
        decision.OutcomeKind, decision.SafeCode, null, registration.Descriptor.Name));
}

if (toolTurns >= context.Profile.MaxToolTurns)
{
    return Complete(new AgentRunOutcome(
        AgentRunOutcomeKind.ValidationError,
        "tool_turn_limit_exceeded",
        null,
        registration.Descriptor.Name));
}

var validation = registration.Tool.Validate(call);
if (!validation.IsValid)
{
    return Complete(new AgentRunOutcome(
        AgentRunOutcomeKind.ValidationError,
        validation.SafeCode!,
        null,
        registration.Descriptor.Name));
}

var invocation = new AgentToolInvocation(runId, context, call);
var result = await registration.Tool.InvokeAsync(invocation, executionSource.Token);
priorResults.Add(result);
toolTurns++;
```

Map profile timeout to the safe `agent_timeout` validation outcome only when caller cancellation was not requested. Propagate caller cancellation as `OperationCanceledException` and unexpected model/tool/observer exceptions unchanged. Observer events contain only IDs, channel, profile ID, tool name, risk, phase and typed outcome; the test observer uses a lock around its event list.

- [ ] **Step 4: Run runner, architecture and full public checks**

Run:

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.ArchitectureTests/Vinx.Erp.ArchitectureTests.csproj --no-restore
./scripts/verify-public.sh
git diff --check
```

Expected: all focused tests and the full public gate pass. If the public gate fails outside changed paths, record the exact command/output in the PR and do not claim the slice is complete.

- [ ] **Step 5: Ask Qwen for read-only diff review, then commit runner slice**

Run:

```bash
git add src/Modules/AI/Vinx.Erp.AI/AgentHarness tests/public/Vinx.Erp.AI.UnitTests
opencode run --dir "$PWD" --model opencode-go/qwen3.8-max --file docs/superpowers/specs/2026-08-11-agent-framework-boundaries-design.md --file docs/superpowers/plans/2026-08-11-agent-harness-core-framework.md "Revisão read-only de #127-C. Inspecione git diff --cached. Procure tool executada antes de policy/validation, timeout que engole cancelamento do caller, retry implícito, dados sensíveis em eventos, isolamento concorrente quebrado ou dependência de host. Não altere arquivos, não faça git write e não acesse quality/private. Liste somente achados P0/P1 com arquivo, linha, motivo e correção."
git commit -m "feat(ai): add supervised harness runner"
```

### Task 4: Document AI ownership and enforce dependency boundary (#127-D)

**Files:**
- Create: `docs/modules/ai.md`
- Create: `tests/public/Vinx.Erp.ArchitectureTests/AiDependencyTests.cs`
- Modify: `tests/public/Vinx.Erp.ArchitectureTests/ModuleContractDocumentationTests.cs`
- Modify: `tests/public/Vinx.Erp.ArchitectureTests/Vinx.Erp.ArchitectureTests.csproj`

**Interfaces:**
- Consumes: AI assembly from Tasks 1–3 and approved framework-boundary spec.
- Produces: durable ownership record and executable constraint that AI has no transport-host dependency.
- Invariant: no code change in #127 introduces a provider package, host registration, endpoint, MCP transport, WhatsApp adapter, persistence, real tool or domain mutation.

- [ ] **Step 1: Write failing architecture and documentation tests**

Add AI module to the required contract-document list. Add a dependency test that loads `Vinx.Erp.AI` assembly and rejects references to the three host assemblies.

```csharp
[Fact]
public void Ai_module_does_not_depend_on_transport_hosts()
{
    var forbidden = new[] { "Vinx.Erp.ApiHost", "Vinx.Erp.McpHost", "Vinx.Erp.WorkerHost" };
    var dependencies = typeof(AgentHarnessRunner).Assembly
        .GetReferencedAssemblies()
        .Select(assembly => assembly.Name)
        .ToHashSet(StringComparer.Ordinal);

    Assert.Empty(dependencies.Intersect(forbidden));
}
```

- [ ] **Step 2: Run architecture tests and observe documentation failure**

Run:

```bash
dotnet test tests/public/Vinx.Erp.ArchitectureTests/Vinx.Erp.ArchitectureTests.csproj --no-restore --filter "FullyQualifiedName~AiDependencyTests|FullyQualifiedName~ModuleContractDocumentationTests"
```

Expected: fail because `docs/modules/ai.md` is absent.

- [ ] **Step 3: Write durable AI module document**

Create `docs/modules/ai.md` using the existing module template headings. Document ownership, one explicit company requirement for commands, contract version `1.0`, no domain deletion in the module and migration note “no persistent schema in #127”. Link the framework-boundary spec; do not copy prompts, provider payloads or temporary test output.

- [ ] **Step 4: Run docs/architecture checks and Qwen diff review**

Run:

```bash
dotnet test tests/public/Vinx.Erp.ArchitectureTests/Vinx.Erp.ArchitectureTests.csproj --no-restore
git diff --check
git add docs/modules/ai.md tests/public/Vinx.Erp.ArchitectureTests
opencode run --dir "$PWD" --model opencode-go/qwen3.8-max --file docs/superpowers/specs/2026-08-11-agent-framework-boundaries-design.md --file docs/superpowers/plans/2026-08-11-agent-harness-core-framework.md "Revisão read-only de #127-D. Inspecione git diff --cached. Procure bypass de ator/empresa/capability, autoexecução de tool, vazamento em eventos, cancelamento incorreto, dependência de host ou inconsistência com spec. Não altere arquivos, não faça git write e não acesse quality/private. Liste somente achados P0/P1 com arquivo, linha, motivo e correção."
```

Resolve each finding válido com teste e rerun; documente no corpo da PR se um achado foi recusado, com evidência concreta. Qwen não aprova a PR sozinho.

- [ ] **Step 5: Commit and prepare stacked PR review**

Run:

```bash
git commit -m "docs(ai): define module boundary"
git diff --check
```

Open one draft PR with the previous core PR as base, list the focused/full public commands, Qwen review result and unresolved risks. Não mesclar até revisão independente e checks remotos.

## Qwen 3.8 Max review protocol

1. **Plano:** anexar spec e este plano ao Qwen, pedir crítica de escopo, dependências, policy order, arquivos e testes. Aplicar apenas achados demonstráveis.
2. **Código:** depois de testes focados, antes de push de cada PR, pedir revisão read-only do diff contra sua base real. Nunca habilitar `--auto`.
3. **Revisão de correção:** se Qwen apontar P0/P1 válido, adicionar teste que reproduza, corrigir, executar o teste e pedir uma revisão curta do diff corrigido.
4. **Evidência:** manter o resumo da revisão no PR, não em arquivos versionados. Nunca colar prompt, resposta integral, token, segredo, documento ou PII.

## Execution handoff

Depois de #127-D integrado, escrever planos separados para #132, #135-A, #135-B, #136-A/B, #126, #133-A/B, #125/#139, #137, #134, #138/#53 e #140. Cada plano deve ser baseado no estado integrado e conter sua própria estimativa, BDD, file map e revisão Qwen antes de código.
