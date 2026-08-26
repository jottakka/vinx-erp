# Agent Access Feedback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bloquear preventivamente o agente quando o bootstrap não autoriza
`ai.read`, explicar o motivo em português simples e eliminar códigos internos
das mensagens apresentadas à pessoa usuária.

**Architecture:** O bootstrap produz apenas um preflight de UX por empresa;
`POST /agent/runs` permanece a autoridade de segurança e cobre revogação
tardia. Um estado discriminado no provider governa conversa, dock, página e
gatilho, enquanto o mapeamento das respostas tipadas converte reason codes em
cópias fixas sem expor snake_case.

**Tech Stack:** React 19, TypeScript 6, Material UI 9, Vitest 4, Testing Library,
Playwright 1.62 e axe-core.

**Spec:** `docs/superpowers/specs/2026-08-25-agent-access-feedback-design.md`

## Global Constraints

- Trabalhar somente na worktree `vinx-erp-agent-access-feedback`, branch
  `codex/agent-access-feedback`; não tocar `main` nem
  `codex/windows-launcher-fix`.
- Nunca acessar `quality/private`.
- TDD obrigatório: registrar RED dos novos comportamentos antes de editar
  produção; não usar a flutuação preexistente do teste de três anexos como RED.
- O browser não concede acesso: o backend continua revalidando `ai.read`.
- Não alterar endpoint, payload, migration, grant ou catálogo canônico.
- Não mostrar reason codes ou error codes internos no texto da conversa.
- Sem botão ou link de solicitação de acesso até existir jornada real.
- Sem dependência nova, retry, persistência ou histórico multi-turn.
- Preservar reset/abort por usuário, membership e empresa.
- Atualizar documentação em português e validar desktop + viewport estreita.

---

### Task 1: Contratos de comportamento e RED

**Files:**
- Modify: `apps/web/src/features/agent/agent.test.tsx`
- Modify: `apps/web/src/features/auth/mock-session-fixtures.ts`

**Interfaces:**
- Produces: fixture explícita de Alfa/Beta com `ai.read` em scope `Company`
  para os cenários autorizados existentes.
- Produces: cenários UI para capability ausente, empresa ausente e negação
  tardia do servidor.

- [ ] **Step 1: Escrever os testes de cópia sem código técnico**

Adicionar casos de `agentReplyText` com resposta `denied`, `text: null`:

```ts
it.each([
  [
    "capability_denied",
    "O assistente não está disponível para o seu perfil nesta empresa. Solicite acesso ao administrador da empresa.",
  ],
  ["company_required", "Selecione uma empresa para usar o assistente."],
  [
    "company_not_authorized",
    "Você não tem acesso ao assistente nesta empresa. Selecione outra empresa ou solicite acesso ao administrador.",
  ],
] as const)("maps %s to actionable copy without exposing the code", ...);
```

Também alterar a expectativa de `document_context_unavailable` para terminar
em `novamente.` e afirmar que o texto não contém snake_case.

- [ ] **Step 2: Escrever RED do preflight sem capability**

Construir gateway estático a partir do snapshot de Maria removendo `ai.read`
da primeira empresa. Abrir o dock e exigir:

```ts
expect(dock.getByRole("status")).toHaveTextContent(
  "O assistente não está disponível para o seu perfil nesta empresa.",
);
expect(dock.getByLabelText("Mensagem para o agente")).toBeDisabled();
expect(dock.getByRole("button", { name: "Anexar" })).toHaveAttribute(
  "aria-disabled",
  "true",
);
expect(dock.getByRole("button", { name: "Enviar" })).toBeDisabled();
expect(fetchSpy).not.toHaveBeenCalled();
```

O teste deve tentar clicar/enviar somente por APIs permitidas pelo DOM; não
reativar elementos com JavaScript.

- [ ] **Step 3: Atualizar o RED sem empresa**

Substituir o caso "keeping text-only sends" por bloqueio completo: mensagem
`Selecione uma empresa para usar o assistente.`, textarea/anexo/envio
desativados e zero requests. Atualizar os textos da página dedicada para não
prometer que o servidor processará um envio bloqueado.

- [ ] **Step 4: Escrever RED da revogação tardia**

Com fixture autorizada, mockar `/agent/runs` como
`denied/capability_denied/text:null`; enviar normalmente e exigir a mensagem
amigável, além de:

```ts
expect(dock).not.toHaveTextContent("capability_denied");
expect(runCalls(fetchSpy)).toHaveLength(1);
```

- [ ] **Step 5: Rodar RED focado**

Run:

```powershell
npm --workspace web run test -- --run src/features/agent/agent.test.tsx -t "access|capability|no company|document context"
```

Expected: FAIL porque o provider ainda permite envio e concatena reason codes.
Guardar no relatório o nome das falhas e confirmar que não é o caso flutuante
de três anexos.

---

### Task 2: Estado de acesso e UI bloqueada

**Files:**
- Modify: `apps/web/src/features/agent/agent-conversation-provider.tsx`
- Modify: `apps/web/src/features/agent/agent-conversation.tsx`
- Modify: `apps/web/src/features/agent/agent-dock.tsx`
- Modify: `apps/web/src/features/agent/agent-page.tsx`
- Modify: `apps/web/src/features/agent/agent-trigger.tsx`
- Test: `apps/web/src/features/agent/agent.test.tsx`

**Interfaces:**
- Consumes: `hasCompanyCapability(company, "ai.read")` de
  `features/auth/company-capabilities.ts`.
- Produces: `AgentAccess`, exposto como `access` no contexto da conversa.
- Preserves: `sendMessage(text): void`, uploads, abort/reset e POST tipado para
  empresas autorizadas.

- [ ] **Step 1: Definir o estado discriminado e as cópias canônicas**

Em `agent-conversation-provider.tsx`, adicionar:

```ts
export type AgentAccess =
  | { available: true; message: null }
  | { available: false; message: string };

const companyRequiredMessage =
  "Selecione uma empresa para usar o assistente.";
const capabilityDeniedMessage =
  "O assistente não está disponível para o seu perfil nesta empresa. Solicite acesso ao administrador da empresa.";

function resolveAgentAccess(
  company: AuthorizedCompanyMembership | null,
): AgentAccess {
  if (!company) return { available: false, message: companyRequiredMessage };
  return hasCompanyCapability(company, "ai.read")
    ? { available: true, message: null }
    : { available: false, message: capabilityDeniedMessage };
}
```

Adicionar `access: AgentAccess` ao context value e ao valor inerte.

- [ ] **Step 2: Impedir efeitos de rede localmente conhecidos**

Calcular `access` uma vez por `ScopedAgentConversationProvider`.
`attachmentSelectionAvailable` deve ser `access.available`. Em
`selectAttachments`, manter a guarda existente por company e disponibilidade.
Em `sendMessage`, retornar antes de criar `AbortController`, limpar anexos,
adicionar mensagem ou obter antiforgery quando `!access.available`.

- [ ] **Step 3: Remover códigos internos das respostas**

Substituir `withReason` por mensagens fixas. Para `denied` com texto nulo, usar:

```ts
function deniedText(reasonCode: AgentRunReasonCode | null): string {
  switch (reasonCode) {
    case "capability_denied":
      return capabilityDeniedMessage;
    case "company_required":
      return companyRequiredMessage;
    case "company_not_authorized":
      return "Você não tem acesso ao assistente nesta empresa. Selecione outra empresa ou solicite acesso ao administrador.";
    default:
      return "Não foi possível executar esta solicitação por uma regra de acesso.";
  }
}
```

`result`, clarification, validation, conflict e unavailable continuam com
mensagens fixas, mas sem `(${reasonCode})` ou `(${code})`. O código permanece
tipado no client e pode decidir a mensagem; não deve ser interpolado.

- [ ] **Step 4: Renderizar o estado bloqueado acessível**

Em `AgentConversation`, quando `!access.available`:

- usar `access.message` no alerta vazio, com `id="agent-access-message"`,
  `role="status"` e severity `warning`;
- ligar o form ao aviso por `aria-describedby="agent-access-message"`;
- desativar textarea, anexo/input e envio;
- não renderizar o alerta redundante "Selecione uma empresa para anexar".

Quando autorizado, preservar a semântica atual, inclusive permitir digitar o
próximo rascunho durante `thinking` e bloquear apenas o envio.

- [ ] **Step 5: Tornar dock, página e gatilho honestos**

No dock, usar `Empresa · Consultas independentes` quando disponível e
`Empresa · Assistente indisponível` quando bloqueado. Na página, trocar
description/processing notice por `access.message` quando bloqueado. No
gatilho, manter o clique para abrir a explicação, mas usar tooltip e nome
acessível `Abrir painel do agente — indisponível para este perfil` no estado
bloqueado.

- [ ] **Step 6: Rodar GREEN de componente**

Run:

```powershell
npm --workspace web run test -- --run src/features/agent/agent.test.tsx
```

Expected: 22 casos existentes ajustados + novos casos PASS. Se o caso de três
anexos voltar a falhar, repeti-lo isoladamente e registrar ambas as execuções;
não alterar produção para acomodar timing de teste.

- [ ] **Step 7: Commit do comportamento**

```powershell
git add apps/web/src/features/agent apps/web/src/features/auth/mock-session-fixtures.ts
git commit -m "fix(agent): gate chat with actionable access feedback"
```

---

### Task 3: Playwright, documentação e gate da implementação

**Files:**
- Modify: `apps/web/e2e/support/bff-session.ts`
- Modify: `apps/web/e2e/module-prototypes.spec.ts`
- Modify: `docs/modules/ai.md`
- Create: `docs/verification/2026-08-25-agent-access-feedback-test-cases.md`
- Modify: `docs/superpowers/specs/2026-08-25-agent-access-feedback-design.md`
- Modify: `docs/superpowers/plans/2026-08-25-agent-access-feedback.md`

**Interfaces:**
- Produces: snapshot Playwright autorizado com `ai.read` explícito.
- Produces: helper de override autenticado do bootstrap sem `ai.read`.
- Produces: cenário E2E bloqueado com desktop, narrow viewport e axe.

- [ ] **Step 1: Escrever a regressão Playwright do estado bloqueado**

Adicionar ao suporte uma função que substitui somente a rota autenticada de
`/foundation/bootstrap`, escolhendo incluir ou remover `ai.read`. O snapshot
padrão dos cenários autorizados inclui:

```ts
{ name: "ai.read", scopes: ["Company"] }
```

No novo cenário, entrar normalmente, instalar override sem a capability,
recarregar e abrir o dock. Exigir aviso, controles desativados, zero chamadas
`/agent/runs`, ausência de `capability_denied`, axe sem violações e
`documentElement.scrollWidth <= innerWidth` após viewport `320x844`.

- [ ] **Step 2: Rodar a validação Playwright focada**

Run apenas o novo caso; o ciclo RED/GREEN obrigatório já foi registrado nos
testes de componente antes da implementação:

```powershell
npm --workspace web run test:e2e:prototypes -- --grep "agent access"
```

Depois rodar os dois cenários existentes de agente para provar que o caminho
autorizado, navegação compartilhada e anexos não regrediram:

```powershell
npm --workspace web run test:e2e:prototypes -- --grep "agent"
```

- [ ] **Step 3: Atualizar contrato de módulo e manifesto**

Em `docs/modules/ai.md`, documentar o preflight do bootstrap, a permanência da
autoridade server-side, o bloqueio sem request e a proibição de reason codes
na UI. O manifesto deve mapear quatro classes: autorizado, sem capability,
sem empresa e revogação tardia, com nomes exatos dos testes Vitest/Playwright.

- [ ] **Step 4: Rodar gates web**

```powershell
npm --workspace web run lint
npm --workspace web run format:check
npm --workspace web run test -- --run
npm --workspace web run build
npm run test:no-direct-supabase
git diff --check
```

Expected: todos PASS. Se a suite completa reproduzir flutuação preexistente,
repetir o caso isolado e reportar o resultado sem ocultar a primeira saída.

- [ ] **Step 5: Commit de E2E e documentação**

```powershell
git add apps/web/e2e docs/modules/ai.md docs/verification/2026-08-25-agent-access-feedback-test-cases.md docs/superpowers
git commit -m "test(agent): verify capability-aware unavailable state"
```

---

### Task 4: Revisão Sol e correções finais

**Files:**
- Review: `git diff origin/main...HEAD`
- Modify: somente arquivos já listados nas Tasks 1–3 quando houver achado
  comprovado.

**Interfaces:**
- Consumes: commits Terra e evidências RED/GREEN.
- Produces: veredito `APPROVED` ou `CHANGES_REQUIRED` com arquivo/linha,
  impacto e teste; quando necessário, patch corretivo e validação focada.

- [ ] **Step 1: Revisar requisito por requisito**

Sol deve verificar: autoridade server-side preservada, zero rede no bloqueio,
revogação tardia amigável, nenhuma interpolação de código, acessibilidade,
path autorizado intacto, abort/reset intactos, textos da página não
enganosos e ausência de mudança de grant/migration.

- [ ] **Step 2: Aplicar apenas correções comprovadas**

Se houver bloqueador, escrever ou ajustar primeiro o teste que o demonstra,
observar a falha, aplicar a correção mínima e rodar o teste focado. Não
refatorar por preferência nem ampliar o escopo.

- [ ] **Step 3: Reexecutar gates afetados e commit**

Rodar os gates da Task 3 afetados pelo patch. Se houver mudanças:

```powershell
git add -- apps/web/src/features/agent/agent-conversation-provider.tsx `
  apps/web/src/features/agent/agent-conversation.tsx `
  apps/web/src/features/agent/agent-dock.tsx `
  apps/web/src/features/agent/agent-page.tsx `
  apps/web/src/features/agent/agent-trigger.tsx `
  apps/web/src/features/agent/agent.test.tsx `
  apps/web/src/features/auth/mock-session-fixtures.ts `
  apps/web/e2e/support/bff-session.ts `
  apps/web/e2e/module-prototypes.spec.ts `
  docs/modules/ai.md `
  docs/verification/2026-08-25-agent-access-feedback-test-cases.md `
  docs/superpowers/specs/2026-08-25-agent-access-feedback-design.md `
  docs/superpowers/plans/2026-08-25-agent-access-feedback.md
git commit -m "fix(agent): address access feedback review"
```

Entregar o veredito e as saídas resumidas ao orquestrador.

---

### Task 5: Gate final, evidência visual e PR único

**Files:**
- Review: `git diff --check origin/main...HEAD`
- Review: `git status --short --branch`

**Interfaces:**
- Produces: screenshots desktop e estreita do estado bloqueado.
- Produces: um PR de `codex/agent-access-feedback` para `main`.

- [ ] **Step 1: Repetir validação independente**

O orquestrador confere os commits e executa lint, format, Vitest, build,
no-direct-Supabase, Playwright de agente e `git diff --check` sem confiar apenas
nos relatos delegados.

- [ ] **Step 2: Inspecionar o runtime real**

Iniciar Vite em porta estrita livre, registrar PID/worktree e abrir o estado
sem `ai.read`. Capturar desktop e `320x844`, confirmar aviso legível, controles
desativados, ausência de overflow e nenhuma chamada `/agent/runs`. Encerrar o
processo dono e confirmar liberação da porta.

- [ ] **Step 3: Conferir Git e publicar**

Confirmar worktree limpa, branch baseada em `origin/main`, diff limitado à
fatia e commits coerentes. Fazer push somente da branch e criar um PR não-draft
para `main` com resumo, testes, evidência visual, nota da flutuação basal e
declaração de que o servidor continua sendo a autoridade. Não mesclar.
