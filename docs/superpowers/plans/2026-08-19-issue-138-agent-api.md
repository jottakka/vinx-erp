# Agente — API supervisionada (#138) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** expor `POST /agent/runs` autenticado para execução supervisionada de leitura/resumo/extração/rascunho não persistido, consumindo o mesmo núcleo (contexto #132, providers #135, guardrails #136) com idempotência por `requestId`, auditoria e respostas tipadas.

**Architecture:** endpoint fino no ApiHost → `AgentExecutionApplicationService` (idempotência + auditoria + orquestração) → `AgentRunner` (política-antes-modelo, agora com o `AgentOutputGate` integrado como passo final). O servidor deriva actor (session gate), capabilities, profile, provider e correlation; o cliente envia SOMENTE mensagem normalizada, empresa quando exigida, jornada e `requestId`. Sem histórico persistido, sem transcript/prompt em qualquer armazenamento, sem retry automático.

**Tech Stack:** .NET 10, ApiHost + módulo `Vinx.Erp.AI` (empilhado no branch do #136 até seu merge), WebApplicationFactory + PostgreSQL real nos testes.

**Spec:** `docs/handoffs/agentic-mvp/priority-agent-api.md` (handoff) · mapa de integração: relatório do code-explorer (SDD workspace, `api-context.md` se anexado).

## Global Constraints

- Request aceita SOMENTE `{ requestId, journey, message, companyId? }` — campos desconhecidos rejeitados; mensagem limitada (1..N caracteres não-whitespace, N=4000); body cap 24 KiB (`ApiConstants.MaximumJsonBodyBytes`); payload bruto NUNCA vai ao módulo AI.
- O servidor deriva tudo: actor via `HttpContext.GetRequiredActorContext()` (session gate + correlation), profile do catálogo server-side, provider de config validada on-start, capability `ai.read` — o cliente nunca envia provider/modelo/capability/veredito.
- ORDEM herdada intacta: validação → contexto fail-closed → gate de provider → UMA chamada de modelo → `AgentOutputGate` (contrato→injection→fontes→grounding→redação) → resposta tipada. Request inválido NUNCA chama provider.
- Resposta tipada com status `result | needs_clarification | denied | validation_error | conflict | requires_confirmation` + `reasonCode` seguro + correlation; texto SOMENTE o redigido; fontes somente referências opacas da empresa autorizada; erro sempre `{"code": "..."}`.
- Idempotência por `requestId` (padrão `IdempotencyRecords`, commandName `agent.run`): replay devolve o MESMO body; hash diferente → 409 `idempotency_conflict`; `ResultJson` armazena somente a resposta segura (status/reason/texto redigido/fontes/correlation) — NUNCA prompt, resposta bruta, transcript ou PII.
- `requires_confirmation` é estado opaco (nome da ação no máximo); NENHUMA execução. Sem confirmação financeira, histórico persistente, memória, webhook, MCP remoto, tool mutável ou provider alternativo.
- Mapeamentos: timeout → `unavailable`/503? NÃO — ver tabela na Task 3 (timeout→504 `agent_timeout`; provider indisponível/não configurado→503 `agent_unavailable`/`provider_not_configured`; cancelamento externo propaga; rate limit → 429 do limiter; SEM retry automático).
- Logs/métricas: operação, resultado, duração, correlation (runner já emite); auditoria `AuditSource.Ai` com metadata `{status, reasonCode}` — sem prompt/resposta/documento/token/PII.
- ApiHost `Program.cs` só registra a superfície (extension method `AddAgentRunSurface`/`MapAgentRunSurface` em `Agent/`); `ApiHost.csproj` ganha ProjectReference a `Vinx.Erp.AI`.
- Mudanças no módulo AI são as MÍNIMAS de composição (justificadas: ai.md reserva DI/composição para #138): adaptador `IAgentModel` sobre `IChatClient`; seleção de provider no perfil aprovado via config; gate integrado ao runner como passo final. Nada de loop/tool/workflow novo.
- Um arquivo por classe; docs PT; `{"code":"..."}`; rate limit `foundation` policy; `TreatWarningsAsErrors`; format whitespace; lockfile commitado se restore mudar.
- Branch: `codex/issue-138-agent-api` a partir de `0dd1f91` (tip do #136 — empilhado; re-target para main quando #256 merger). Worktree: `.worktrees/agent-api`.

## Estimativa

~14 caminhos (3 novos em ApiHost/Agent + Program.cs + csproj + 2 no módulo AI + 1 extensão + 2 testes unit + 1 integração + 2 docs). ~800 linhas manuais. Zero migrações (idempotência usa tabela existente).

---

### Task 1: Núcleo pronto para composição (módulo AI, mínimo)

**Files:**
- Create: `src/Modules/Vinx.Erp.AI/Providers/IChatClientAgentModel.cs`
- Modify: `src/Modules/Vinx.Erp.AI/AgentTaskProfile.cs` (campos `SourcesRequired = false` e `RequiredOutputFields = null` opcionais no fim do record)
- Modify: `src/Modules/Vinx.Erp.AI/AgentTaskProfileCatalog.cs` (`CreateApprovedProfile(AgentModelSelection? selection)` — valores do catálogo + seleção server-side)
- Modify: `src/Modules/Vinx.Erp.AI/AgentRunner.cs` (ctor ganha `AgentOutputGate? outputGate = null`; após a chamada do modelo, quando o gate está presente e houve texto/tool proposal, constrói `AgentModelResponse` a partir da `ChatResponse` e do contexto resolvido, roda `Evaluate` com contrato do perfil e mapeia: Result→`Completed`(reason do gate), NeedsClarification→`NeedsClarification`, Denied→`Denied`, ValidationError→`Invalid`; `Text` vira `RedactedText`; `ProposedActionName` preservado)
- Test: `tests/public/Vinx.Erp.AI.UnitTests/AgentRunnerGateIntegrationTests.cs`
- Test: estende `AgentTaskProfileTests.cs` (defaults dos campos novos; `CreateApprovedProfile` com/sem seleção)

**Interfaces:**
```csharp
// Providers/IChatClientAgentModel.cs
/// <summary>Adapta IChatClient aprovado à fronteira IAgentModel; uma chamada, sem tools.</summary>
public sealed class IChatClientAgentModel(IChatClient chatClient) : IAgentModel;

// Catalog
public static AgentTaskProfile CreateApprovedProfile(AgentModelSelection? modelSelection); // read_only_summarize_v1 + seleção
```
- Mapeamento ChatResponse→AgentModelResponse DENTRO do runner (único lugar): `Text` = primeiro texto; `HasToolProposal` = presence de FunctionCallContent; `StructuredJson` = null (v1); `CitedSources` = vazio (v1 — o modelo não cita fontes estruturadas ainda; o gate mantém as regras para quando citar); `Confidence` = null.
- Sem gate configurado (null): comportamento atual inalterado (compat retrógrado com todos os testes existentes).

- [ ] RED: runner com gate — resposta com e-mail → outcome `Completed` com `Text` contendo `[email-redigido]`; tool proposal + gate → `RequiresConfirmation` preservado com texto redigido; injection no texto → `Denied`/`prompt_injection_detected`; sem gate → comportamento atual. Profile defaults. `CreateApprovedProfile` com seleção valida e sem seleção deixa `provider_not_configured` (runner inalterado nesse ponto).
- [ ] GREEN; build/format; commit `feat(ai): compose output gate into runner with provider selection`.

---

### Task 2: Contratos HTTP e parser estrito

**Files:**
- Create: `src/Hosts/Vinx.Erp.ApiHost/Agent/AgentHttpContracts.cs`
- Test: `tests/public/Vinx.Erp.Foundation.UnitTests/AgentHttpContractTests.cs`

**Interfaces:**
```csharp
public sealed record AgentRunHttpRequest(Guid RequestId, string Journey, string Message, Guid? CompanyId);
public sealed record AgentRunHttpResponse(
    string Status,            // result|needs_clarification|denied|validation_error|conflict|requires_confirmation
    string? ReasonCode,
    string? Text,             // somente o redigido
    IReadOnlyList<AgentRunSourceRef>? Sources,   // { Reference } opaco
    Guid CorrelationId);
public static class AgentHttpContract
{
    public const int MaxMessageLength = 4000;
    public static bool TryParse(Stream body, long maxBytes, out AgentRunHttpRequest? request, out string? errorCode);
    // JSON estrito: objeto raiz; campos fechados {requestId,journey,message,companyId}; desconhecido → "invalid_request";
    // tipos exatos (Guid/string); message 1..4000 após trim; journey não-vazio ≤100; companyId opcional Guid.
}
```
- [ ] RED (unit, sem host): válido; campo desconhecido; falta campo; tipo errado (requestId não-Guid); message vazia/whitespace; message > 4000; body > maxBytes; JSON malformado; companyId null ok.
- [ ] GREEN; commit `feat(api): strict agent run http contract`.

---

### Task 3: Serviço de aplicação com idempotência e auditoria

**Files:**
- Create: `src/Hosts/Vinx.Erp.ApiHost/Agent/AgentExecutionApplicationService.cs`
- Test: coberto pela Task 5 (integração — serviço exige Postgres real: session gate, RLS, idempotência, audit)

**Interfaces:**
```csharp
public sealed class AgentExecutionApplicationService(
    FoundationDbContext database,
    IAuditAppender audit, // AuditAppender
    AgentRunner runner,
    AgentRunner? — não; deps: runner, database, AuditAppender, IOptions<AgentRunOptions>)
{
    public async Task<AgentRunHttpResponse> RunAsync(ActorContext actor, AgentRunHttpRequest request, CancellationToken ct);
}
```
- `AgentRunOptions` (em Agent/, validada on-start): `Provider` ("openai_compatible"|"deepseek"|null), `Endpoint`, `ApiKey` (env-only, nunca em log), `Model`, `Timeout`, `AllowedLinkHosts` (para o redator do gate), `MaxConcurrentRuns`? (YAGNI — não). Sem provider → perfil sem seleção → `provider_not_configured` (503). **Secret nunca em log/config dump.**
- Fluxo: idempotência (`commandName="agent.run"`; payloadHash = SHA256 de `{journey,message,companyId}` normalizado — SEM prompt no hash? o hash É derivado da mensagem mas armazena só o hash — ok, padrão do repo) → replay (mesmo body; SEM nova chamada) / conflito (409 `idempotency_conflict`) → `runner.RunAsync(actor, profileId, message, companyId)` → mapear outcome→HTTP:
  - Completed→200 `result`; NeedsClarification→200 `needs_clarification`; Denied→200 `denied` (403? handoff diz resposta tipada denied — usar 200 com status denied, corpo seguro; Access BDD não exige 4xx) — **Ruling: 200 + status tipado** (o contrato do handoff é de statuses na resposta, não de HTTP codes semânticos; conflito é o único 409; timeout 504 `agent_timeout`; Unavailable 503 `agent_unavailable`/`provider_not_configured`; Invalid→400 `validation_error`).
  - RequiresConfirmation→200 `requires_confirmation` + `ReasonCode` da ação (nome opaco).
- Auditoria: sucesso e denied (AuditSource.Ai, action `agent.run`, metadata `{status, reasonCode}`) na mesma transação do registro de idempotência; denials auditadas em transação própria (padrão ReturnAuditedDenialAsync).
- `ResultJson` do idempotency = resposta segura serializada (status/reason/text redigido/sources/correlation).

- [ ] Implementar + commit `feat(api): agent run application service with idempotency and audit` (a validação de comportamento é a Task 5; unit tests aqui limitam-se ao mapeamento outcome→HTTP puro se extraído como função estática — prefira função pura `MapOutcome(AgentOutcome) → (status, http, reason)` testável em unit dentro do próprio arquivo).

---

### Task 4: Endpoint, DI e composição

**Files:**
- Create: `src/Hosts/Vinx.Erp.ApiHost/Agent/AgentEndpoints.cs` (`MapAgentRunSurface(this IEndpointRouteBuilder)`: `POST /agent/runs` — RequireAuthorization, rate limit `foundation`, hand-parse com `AgentHttpContract.TryParse` + cap `ApiConstants.MaximumJsonBodyBytes`, `GetRequiredActorContext()`, resposta JSON com `no-store`)
- Create: `src/Hosts/Vinx.Erp.ApiHost/Agent/AgentRunServiceCollectionExtensions.cs` (`AddAgentRunSurface(IConfiguration)`: ProjectReference implícito; `AddHttpClient`+`OpenAiCompatibleChatClient`/`DeepSeekChatClient` conforme `AgentRunOptions.Provider`; `IChatClientAgentModel`; gates (`SensitiveOutputRedactor(AllowedLinkHosts)`); `AgentRunner` com gate; `AgentExecutionApplicationService`; options `ValidateOnStart`)
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Program.cs` (2 linhas: `AddAgentRunSurface`; `MapAgentRunSurface`)
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Vinx.Erp.ApiHost.csproj` (ProjectReference Vinx.Erp.AI)

- [ ] RED leve: teste de composição unit — ServiceProvider com config válida resolve `AgentExecutionApplicationService` e `AgentRunner` com gate; SEM provider → resolve e runner fica `provider_not_configured` (o fail-closed default é testável); config inválida (provider desconhecido/sem endpoint) → falha na validação on-start.
- [ ] GREEN; build/format; commit `feat(api): authenticated agent run endpoint with server-side composition`.

---

### Task 5: Testes de integração BDD e documentação

**Files:**
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj` (ProjectReference Vinx.Erp.AI)
- Test: `tests/public/Vinx.Erp.Foundation.IntegrationTests/AgentEndpointTests.cs`
- Create: `docs/verification/issue-138-agent-api-test-cases.md`
- Modify: `docs/runbooks/local-development.md` (executar sem key: provider ausente → `provider_not_configured` 503; com key via env)
- Modify: `docs/modules/ai.md` (seção da API: superfície, ordem completa endpoint→gate, runbook pointer; bump 2.3→2.4)

- [ ] RED→GREEN integração (FoundationApiFactory + fake `IAgentModel` override no DI do teste; sem provider real):
  1. `Valid_run_returns_result_with_redacted_text_and_correlation` (sucesso; Alfa; texto com e-mail sai redigido).
  2. `Empty_message_returns_validation_error_without_provider_call` (fake CallCount 0).
  3. `Unknown_json_field_is_rejected` / `Message_too_long` / `Missing_company_for_company_scoped_journey` → needs_clarification.
  4. `Forged_company_denies_without_consulting_beta`.
  5. `Anonymous_request_is_unauthorized` (401 sem token).
  6. `Same_requestId_replays_same_body` / `Different_payload_same_requestId_conflicts_409`.
  7. `Provider_not_configured_returns_503_agent_unavailable`.
  8. `Timeout_maps_to_504_agent_timeout` (fake com delay + options Timeout curto).
  9. `Requires_confirmation_returns_opaque_state_without_execution`.
  10. `Parallel_requests_isolate_outcomes_and_correlations` (2 atores/empresas concorrentes).
  11. Auditoria: evento `agent.run` success + denied com metadata segura; idempotency record SEM prompt (assert conteúdo do ResultJson).
- [ ] Docs (matriz BDD + runbook + ai.md).
- [ ] Verificações: AI UnitTests + Foundation UnitTests + IntegrationTests (filtro Agent + DocumentUpload) + Arch + format + diff-check.
- [ ] Commit `feat(api): agent run endpoint integration tests and docs`.

---

## Verificação final (freeze — serializado)

1. `./scripts/verify-public.sh` EXIT:0 no HEAD exato (protocolo de flake solo+cooldown; máquina compartilhada com outras sessões).
2. Revisão final de branch inteira: autenticação/tenant/rate limit/confirmação/idempotência; sem prompt/transcript/PII em qualquer camada; parser estrito; provider fail-closed default.
3. `git diff --check`; PR draft com base no branch do #136 (re-target main após merge) + evidência completa.
