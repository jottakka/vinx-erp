# Agente — guardrails de saída (#136) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** impedir que a saída do modelo seja tratada como verdade: um gate determinístico valida schema, autoriza fontes, redige conteúdo sensível, exige grounding e trata prompt injection antes de qualquer canal (UI/WhatsApp/MCP) mostrar o resultado.

**Architecture:** o gate recebe `AgentModelResponse` (contrato normalizado do módulo, construído a partir da fronteira `Microsoft.Extensions.AI` já existente) + `AgentExecutionContext` confiável (já existente da #132), aplica policy versionada e retorna `result`, `needs_clarification`, `denied` ou `validation_error`. O gate COMPLEMENTA, não substitui, autorização de tool e serviço de domínio. Tudo determinístico e testável sem provider.

**Tech Stack:** .NET 10, módulo `Vinx.Erp.AI` existente no main (`4121a37`), `System.Text.Json` para parse estrito, regex determinísticas para redação, testes públicos xUnit.

**Spec:** `docs/handoffs/agentic-mvp/priority-agent-output-guardrails.md` (handoff) · invariantes de `docs/superpowers/specs/2026-08-11-agent-framework-boundaries-design.md` seção 4.

## Global Constraints

- A saída do modelo NUNCA é verdade por si: schema inválido, campo desconhecido, fonte não autorizada, evidência ausente, confiança baixa ou injection detectada NÃO chegam ao canal.
- Fontes têm escopo de empresa: fonte de outra empresa citada pelo ator é REMOVIDA e gera negação/abstenção (nunca vaza nem confirma).
- Sem fonte exigida pela jornada → `needs_clarification`/abstenção; o gate NUNCA completa lacunas.
- Redação ANTES de qualquer canal: identificadores (CPF/CNPJ-like), e-mail, dados bancários, conteúdo médico e links não allow-listados viram tokens opacos; somente referências opacas autorizadas permanecem.
- Prompt injection é conteúdo não confiável: detecção heurística determinística (marcadores PT/EN); texto de documento/usuário NUNCA é promovido a instrução do sistema.
- `requires_confirmation` permanece ESTADO (`HasToolProposal` preservado no resultado); nenhuma mutação/execução.
- Nada de: moderação médico/jurídica automática, autorização de tool, memória, chamada a provider, novo pacote.
- ORDEM fixa do gate: contrato (schema) → prompt injection → autorização de fontes → grounding/confiança → redação → resultado com `AgentPolicyVersion.Current`.
- Um arquivo por classe (handoff lista os 5); namespace `Vinx.Erp.AI.Guardrails`; net10.0; `TreatWarningsAsErrors`; `dotnet format whitespace --verify-no-changes`; docs PT compactas; fixtures sintéticas SEM prompts reais ou PII.
- Branch: `codex/issue-136-output-guardrails` a partir de `origin/main` (`4121a37`). Worktree: `.worktrees/agent-output-guardrails`.

## Estimativa

~12 caminhos (5 fontes em Guardrails/, 5 arquivos de teste em Guardrails/, 2 docs). ~600–750 linhas manuais. Zero migrações, zero pacotes, zero mudança em hosts.

---

### Task 1: Contrato de saída, policy versionada e gate com validação de schema

**Files:**
- Create: `src/Modules/Vinx.Erp.AI/Guardrails/AgentOutputContract.cs`
- Create: `src/Modules/Vinx.Erp.AI/Guardrails/AgentPolicyVersion.cs`
- Create: `src/Modules/Vinx.Erp.AI/Guardrails/AgentOutputGate.cs`
- Test: `tests/public/Vinx.Erp.AI.UnitTests/Guardrails/AgentOutputContractTests.cs`
- Test: `tests/public/Vinx.Erp.AI.UnitTests/Guardrails/AgentOutputGateSchemaTests.cs`

**Interfaces:**
- Consumes: `AgentExecutionContext` existente (`Vinx.Erp.AI`: ActorId, CorrelationId, CompanyId, CompanyName, MembershipId, ProfileId, Capabilities).
- Produces (Tasks 2–4 dependem):

```csharp
namespace Vinx.Erp.AI.Guardrails;

/// Resposta normalizada do modelo consumida pelo gate (construída fora daqui a partir da ChatResponse).
public sealed record AgentModelResponse(
    string? Text,
    string? StructuredJson,
    IReadOnlyList<AgentCitedSource> CitedSources,
    double? Confidence,
    bool HasToolProposal);

/// Fonte citada com escopo de empresa; referência opaca controlada pelo servidor.
public sealed record AgentCitedSource(string Reference, Guid CompanyId);

public sealed record AgentContractValidation(bool IsValid, string ReasonCode);
// "contract_valid" | "structured_json_invalid" | "required_field_missing:<campo>" | "unknown_field:<campo>"

/// Contrato estrito do payload estruturado por jornada.
public sealed class AgentOutputContract(string journey, IReadOnlySet<string> requiredFields)
{
    public string Journey { get; }
    public AgentContractValidation Validate(string? structuredJson);
    // System.Text.Json; UnknownMemberHandling disallow; campos obrigatórios; só objeto raiz.
}

public static class AgentPolicyVersion
{
    /// Versão datada da política de saída; registrada em todo resultado do gate.
    public const string Current = "2026-08-19.1";
}

public enum AgentOutputGateStatus { Result, NeedsClarification, Denied, ValidationError }

public sealed record AgentOutputGateResult(
    AgentOutputGateStatus Status,
    string ReasonCode,
    string? RedactedText,
    IReadOnlyList<AgentCitedSource> AuthorizedSources,
    string PolicyVersion,
    bool HasToolProposal);
// reason codes (constantes na classe): ok, schema_invalid, requires_confirmation_state, prompt_injection_detected,
// cross_tenant_source, no_authorized_source, low_confidence, no_evidence

public sealed class AgentOutputGate(SourceAuthorizationGate sources, SensitiveOutputRedactor redactor)
{
    public AgentOutputGateResult Evaluate(AgentOutputGateRequest request);
}

public sealed record AgentOutputGateRequest(
    AgentExecutionContext Context,
    AgentModelResponse Response,
    AgentOutputContract Contract,
    bool SourcesRequired);
```

- Nesta task o gate valida SÓ o contrato (steps 1–2 da ordem): schema inválido → `ValidationError`/`schema_invalid` (texto não chega ao canal); texto+schema válidos → `Result`/`ok` com texto passado pelo redator (Task 3 fornece o redator REAL; aqui um redator identidade temporário? NÃO — sem stubs: nesta task o gate recebe o redator via ctor e Tasks 2–4 entregam os componentes; para manter a branch compilando e testável, Task 1 entrega `SourceAuthorizationGate` e `SensitiveOutputRedactor` com implementação MÍNIMA real: fonte autorizada = mesmo CompanyId (autorização completa real — é pequena) e redator com o caso e-mail (o resto na Task 3). Assim cada task adiciona comportamento real sem stubs.
- `HasToolProposal` sempre espelhado no resultado (estado preservado; reason code principal permanece o da avaliação).

- [ ] **Step 1: RED — `AgentOutputContractTests`**: (1) payload válido com campos obrigatórios → valid; (2) campo obrigatório ausente → `required_field_missing:<campo>`; (3) campo desconhecido → `unknown_field:<campo>`; (4) JSON malformado/não-objeto → `structured_json_invalid`; (5) StructuredJson null com jornada que exige campos → `structured_json_invalid`; (6) null com conjunto de obrigatórios vazio → `contract_valid`.
- [ ] **Step 2: RED — `AgentOutputGateSchemaTests`**: (1) resposta estruturada válida → `Result`/`ok`, PolicyVersion == Current, fonte autorizada inclusa; (2) schema inválido → `ValidationError`/`schema_invalid` e `RedactedText` null (nada chega ao canal); (3) `HasToolProposal` true → preservado no resultado qualquer que seja o status.
- [ ] **Step 3: Run RED** — compilação falha (tipos não existem).
- [ ] **Step 4: GREEN** — implementar os 3 arquivos (+ os 2 mínimos reais de SourceAuthorizationGate/SensitiveOutputRedactor, que a Task 2/3 expandem).
- [ ] **Step 5: Run GREEN** + `dotnet build Vinx.Erp.sln` limpo + `dotnet format whitespace`.
- [ ] **Step 6: Commit** — `feat(ai): output guardrail contract with versioned policy gate`.

---

### Task 2: Autorização de fontes, grounding e confiança

**Files:**
- Create: `src/Modules/Vinx.Erp.AI/Guardrails/SourceAuthorizationGate.cs` (completo — substitui o mínimo da Task 1)
- Modify: `src/Modules/Vinx.Erp.AI/Guardrails/AgentOutputGate.cs` (steps 4–6 da ordem)
- Test: `tests/public/Vinx.Erp.AI.UnitTests/Guardrails/SourceAuthorizationGateTests.cs`
- Test: `tests/public/Vinx.Erp.AI.UnitTests/Guardrails/AgentOutputGateGroundingTests.cs`

**Interfaces:**
```csharp
public sealed record SourceAuthorizationResult(
    IReadOnlyList<AgentCitedSource> AuthorizedSources,
    IReadOnlyList<AgentCitedSource> RemovedSources,
    bool CrossTenantViolation);

public sealed class SourceAuthorizationGate
{
    public SourceAuthorizationResult Authorize(AgentExecutionContext context, IReadOnlyList<AgentCitedSource> sources);
    // Autorizada: source.CompanyId == context.CompanyId. Outra empresa: removida + violação.
}
```
Regras do gate (após schema/injection): violação cross-tenant → `Denied`/`cross_tenant_source` (fonte removida jamais exposta); `SourcesRequired` e nenhuma fonte autorizada restante → `NeedsClarification`/`no_authorized_source` (abstenção — nunca completa lacunas); `Confidence < AgentOutputGate.MinimumConfidence` (constante 0.5) → `NeedsClarification`/`low_confidence`; texto vazio sem fontes → `NeedsClarification`/`no_evidence`.

- [ ] **Step 1: RED — `SourceAuthorizationGateTests`**: (1) fonte da própria empresa autorizada; (2) fonte Beta (outra empresa) removida + `CrossTenantViolation`; (3) lista vazia → sem autorizadas, sem violação.
- [ ] **Step 2: RED — `AgentOutputGateGroundingTests`** (contexto Alfa; fontes com CompanyId Alfa/Beta): (1) resposta fundamentada em Alfa → `Result`/`ok` somente com fontes Alfa; (2) citação de Beta → `Denied`/`cross_tenant_source`, AuthorizedSources vazio, Beta ausente de TODO o resultado; (3) `SourcesRequired` + zero fontes → `NeedsClarification`/`no_authorized_source`; (4) `Confidence=0.2` → `NeedsClarification`/`low_confidence`; (5) texto vazio + sem fontes → `NeedsClarification`/`no_evidence`; (6) `Confidence=null` NÃO derruba (sem confiança reportada não há base para abster).
- [ ] **Step 3: Run RED; Step 4: GREEN; Step 5: build/format; Step 6: Commit** — `feat(ai): source authorization grounding and confidence gate`.

---

### Task 3: Redação determinística de conteúdo sensível

**Files:**
- Create: `src/Modules/Vinx.Erp.AI/Guardrails/SensitiveOutputRedactor.cs` (completo — substitui o mínimo)
- Test: `tests/public/Vinx.Erp.AI.UnitTests/Guardrails/SensitiveOutputRedactorTests.cs`
- Test: estende `AgentOutputGateGroundingTests` (ou novo `AgentOutputGateRedactionTests.cs`): texto do `Result` sai SEMPRE redigido.

**Interfaces:**
```csharp
public sealed class SensitiveOutputRedactor(IReadOnlySet<string> allowedLinkHosts)
{
    public RedactionResult Redact(string text);
    // E-mails → [email-redigido]; CPF/CNPJ-like (11/14 dígitos formatados ou não) → [identificador-redigido];
    // dados bancários (agência/conta/IBAN-like) → [dado-bancario-redigido]; marcadores médicos (lista fechada
    // de termos/ICD-like) → [conteudo-medico-redigido]; URLs não allow-listed → [link-removido];
    // URL allow-listed permanece. Tokens em PT, determinísticos, sem eco do conteúdo original.
}
public sealed record RedactionResult(string RedactedText, bool RedactedSomething);
```
- Gate: no caminho `Result`, o texto exposto é SEMPRE `redactor.Redact(...)`; redação nunca altera status.
- [ ] **Step 1: RED** — casos: e-mail; CPF formatado e cru; CNPJ; conta bancária `12345-6`/agência; IBAN-like; termo médico da lista; URL de host allow-listado permanece; URL de host não listado removida; texto limpo inalterado (`RedactedSomething=false`); nenhum token ecoa o dado original.
- [ ] **Step 2: RED gate** — resposta válida com e-mail no texto → `Result` com `RedactedText` contendo `[email-redigido]` e SEM o e-mail.
- [ ] **Step 3: Run RED; Step 4: GREEN; Step 5: build/format; Step 6: Commit** — `feat(ai): deterministic sensitive output redaction`.

---

### Task 4: Prompt injection, isolamento concorrente e estado de confirmação

**Files:**
- Modify: `src/Modules/Vinx.Erp.AI/Guardrails/AgentOutputGate.cs` (step 3 da ordem: injection ANTES de fontes)
- Test: `tests/public/Vinx.Erp.AI.UnitTests/Guardrails/AgentOutputGateAdversarialTests.cs`

**Interfaces:**
- Detecção: lista fechada de marcadores PT/EN (constante `InjectionMarkers`): p.ex. "ignore as instruções", "ignore all instructions", "disregard previous", "system prompt", "reveal your prompt", "mostre seu prompt", "you are now", "a partir de agora você é". Match ordinal ignore-case sobre o texto → `Denied`/`prompt_injection_detected`. Heurística determinística documentada como defesa-em-profundidade (nunca autorização); texto de documento/usuário nunca vira instrução — o gate só lê como dados.
- `requires_confirmation` continua estado puro: resposta com `HasToolProposal` + texto válido → resultado carrega o estado + o texto redigido; NENHUM caminho executa algo.

- [ ] **Step 1: RED** — (1) texto com marcador de injection → `Denied`/`prompt_injection_detected`, texto não exposto (`RedactedText` null); (2) injection com fonte cross-tenant juntas → injection vence (ordem fixa: 2 antes de 4); (3) texto limpo com marcador legítimo parecido ("instruções do produto") → passa (sem falso positivo com a lista fechada); (4) `HasToolProposal` + tudo válido → `Result`/`ok` + estado preservado + texto redigido; (5) concorrência: dois gates/policies (Alfa e Beta) avaliando em paralelo (`Task.WhenAll` sobre evaluates independentes) → fontes/resultados 1:1 sem mistura; (6) PolicyVersion sempre presente.
- [ ] **Step 2: Run RED; Step 3: GREEN; Step 4: build/format; Step 5: Commit** — `feat(ai): prompt injection guard with concurrent tenant isolation`.

---

### Task 5: Documentação de verificação e módulo

**Files:**
- Create: `docs/verification/issue-136-output-guardrails-test-cases.md`
- Modify: `docs/modules/ai.md` (SOMENTE contrato/versionamento da política de saída)

- [ ] Matriz BDD 5 classes mapeando cada teste das Tasks 1–4 (sucesso/validação/acesso/estado/concorrência) + limites declarados: heurística de injection é defesa-em-profundidade (lista fechada), redação é por padrões determinísticos (não moderação semântica), integração com API/canal (#138) fica pendente.
- [ ] `ai.md`: seção do contrato de saída com `AgentPolicyVersion` e a ordem fixa do gate; sem prometer canal/provider.
- [ ] Verificações: `dotnet test tests/public/Vinx.Erp.AI.UnitTests` + `tests/public/Vinx.Erp.Foundation.UnitTests` + ArchitectureTests verdes; `dotnet format whitespace --verify-no-changes`; `git diff --check`.
- [ ] Commit — `docs(ai): output guardrails verification cases and module contract`.

---

## Verificação final (freeze — serializado)

1. `./scripts/verify-public.sh` EXIT:0 no HEAD exato (serializado; protocolo de flake solo+cooldown).
2. Revisão final de branch inteira: ordem do gate, nada vaza cross-tenant, redação antes de canal, injection como dado, estado de confirmação puro, fixtures sem PII/prompts reais.
3. `git diff --check`; PR draft com evidência (veredito, BDD, gate, heads).
