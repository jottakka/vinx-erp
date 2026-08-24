# Plano — Chat do FE na API real + imagens no contexto do agente (rev. 2 pós-Sol)

Data: 2026-08-23. Base: `origin/main` `b3b12d6`. Worktree:
`.worktrees/fe-chat-media-context`, branch `codex/fe-chat-media-context`.
Issue proprietária: #53 ([Assistant][V1] UI supervisionada).
Revisão Sol rev.1 (2026-08-23, `/tmp/vinx-agent-fe-sol-plan-review.md`):
`CHANGES_REQUIRED` com 5 bloqueadores e decisões (a)–(e) — todos
incorporados nesta revisão; citações Sol verificadas manualmente.

## 1. Verificação do estado atual (fatos, arquivo:linha)

### 1.1 O chat do FE está conectado à API de chat? — NÃO

- O chat do agente no FE é um mock local: `sendMessage` insere a mensagem do
  usuário e devolve uma resposta demonstrativa fixa após `setTimeout(600ms)`
  (`apps/web/src/features/agent/agent-conversation-provider.tsx:100-133`,
  `demoReply`/`demoSources`/`demoProposal` linhas 17-30). Nenhuma chamada HTTP.
- O workspace só existe em DEV (`isAgentWorkspaceEnabled() = import.meta.env.DEV`,
  linhas 61-63); em produção o provider é inerte.
- A API de chat existe e está completa no BE: `POST /agent/runs`
  (`AgentEndpoints.cs:29-45`) com autorização, rate limit, parse estrito,
  idempotência por `requestId`, auditoria Ai e guardrails de saída.
- O FE não tem NENHUM client para `/agent/runs` (grep em `apps/web/src`).

### 1.2 É possível adicionar imagens/figuras no contexto? — NÃO (nem FE nem BE)

- Contrato HTTP text-only: `AgentRunHttpRequest(Guid RequestId, string
  Journey, string Message, Guid? CompanyId)`, `Message` string ≤ 4000
  (`AgentHttpContracts.cs:31-32,47`); campos fechados (linha 56).
- Runner stateless single-turn: UMA `ChatMessage` por run
  (`AgentRunner.cs:65`); sem histórico persistido.
- Contrato FE text-only: `AgentMessage` só tem `text: string`
  (`agent-contract.ts:7-12`).
- Visão/OCR existem no módulo AI mas são inalcançáveis do chat: perfis
  OpenAI-compatible `AllowImageInputs` (#135-B) e pipeline
  `DocumentContext/*` (#134) sem consumo em `/agent/runs`.
- `IAuthorizedDocumentSource` real (#134-A) NÃO implementado — só fakes;
  OCR fail-closed em runtime.
- Upload existe no BE sem ligação com o agente: `/files/upload-intentions` +
  confirm (`Program.cs:704-724`); FE sem client de upload.
- Bloqueios operacionais (dono, não código FE): capability `ai.read` não
  semeada (constante em `AgentCapabilities.cs`; journey único
  `read_only_summarize_v1`, `AgentTaskProfileCatalog.cs:13`) e config de
  provider ausente no ambiente — o REGISTRO condicional já existe
  (`AgentRunServiceCollectionExtensions.cs:55-82`; sem config o runner
  fail-closed `provider_not_configured`).

## 2. Fatia 1 — Ligar o chat do FE ao POST /agent/runs (texto) — ESTA FATIA

**Goal:** substituir o mock por chamadas reais ao BFF no padrão dos clients
existentes, com mapeamento fiel do contrato tipado, ciclo de vida assíncrono
fechado e UI honesta sobre empresa/estado. Sem mudanças no BE.

### 2.1 Client (`apps/web/src/features/agent/agent-client.ts`, novo)

- Transporte: reusar `fetchSameOriginBff` e `readAntiforgeryTokenBff` de
  `shared/http/bff-response.ts:89-119,138-173` (no-store, same-origin,
  redirect proibido, abort/rede mapeados em falha tipada — nunca exceção
  vazando `Error.message`).
- Antiforgery: GET `/foundation/identity/browser-session/antiforgery` +
  header `X-CSRF-TOKEN` (padrão `atendimento-client.ts:18-51`). Falha do
  token → resultado tipado, mensagem fixa.
- Corpo: `{ requestId: crypto.randomUUID(), journey:
  "read_only_summarize_v1", message, ...("companyId" somente quando
  existir) }` — sem `companyId` quando não houver empresa: o POST ACONTECE e
  o servidor responde `needs_clarification/company_required` (autoridade do
  servidor; o FE não fabrica essa resposta).
- Parse por status code, com schemas estritos separados (campos presentes e
  ANULÁVEIS, não opcional-ausente; `sources` é array de `{ reference }` —
  `AgentHttpContracts.cs:28-40`):
  - **200:** tipado `{ status: result|needs_clarification|denied|
    requires_confirmation, reasonCode: string|null, text: string|null,
    sources: {reference: string}[]|null, correlationId: uuid }` (zod
    `.strict()` com nullable explícito);
  - **400:** DOIS formatos reais — tipado `validation_error` do endpoint
    (`AgentEndpoints.cs:98-109`) OU envelope antiforgery
    `{"code":"invalid_request"}` emitido ANTES do endpoint
    (`BrowserBffRequestMiddlewares.cs:66-74`); tentar schema tipado e cair
    no envelope `{"code"}` com allow-list `invalid_request`;
  - **409:** tipado `conflict`;
  - **401/429:** NÃO pressupor corpo — resultado tipado por status;
  - **503/504:** envelope `{"code"}` com allow-list
    (`provider_not_configured` | `agent_unavailable` | `audit_unavailable` |
    `agent_timeout`);
  - qualquer corpo fora do schema → falha `protocol` com mensagem fixa
    (nunca exibir/logar body bruto, Zod error, mensagem enviada ou prompt).
- `reasonCode` do contrato tipado em enum explícito (fonte: matriz
  `docs/verification/issue-138-agent-api-test-cases.md:32-49`): `ok`,
  `tool_proposal_requires_confirmation`, `capability_denied`,
  `company_not_authorized`, `company_required`, `message_empty`,
  `message_too_long`, `request_id_invalid`, `journey_invalid`,
  `invalid_request`, `idempotency_conflict`, `integrity_conflict`.
  Desconhecido → tratado como genérico (nunca ecoado cru na UI).
- Sem retry NUNCA: rede, abort, 429, 503, 504 — resultado final do turno.
  Abort de POST é potencialmente ambíguo (pode ter cometido): nenhum
  reenvio automático; o `requestId` é novo a cada envio aceito.

### 2.2 Provider (`agent-conversation-provider.tsx`, editado)

- Ciclo de vida assíncrono fechado (substitui o cancelamento do
  `setTimeout` atual, linhas 75-98): `AbortController` + contador de
  geração/epoch em ref + trava síncrona de in-flight (o `sendMessage` já
  retorna cedo quando ocupado — manter semântica):
  - reset/unmount aborta a chamada corrente e invalida o resultado tardio
    (epoch check antes de qualquer `setState`);
  - troca de `companyId` resolvido → aborta + limpa conversa (mensagem de
    outra empresa nunca entra no contexto visual de outra);
  - dupla submissão rápida → exatamente 1 POST (trava síncrona);
  - cada envio aceito gera exatamente 1 `requestId` novo.
- Mapeamento contrato → UI (mensagem do agente; texto do servidor quando
  `text != null`; senão mensagem fixa + `reasonCode` do enum):
  - `result` → texto + fontes (`sources[].reference` como label do Chip);
  - `needs_clarification`/`denied` → `text` quando presente (v1:
    `company_required`/`capability_denied` vêm com `text=null` — mensagem
    fixa + reasonCode);
  - `requires_confirmation` → mensagem fixa "confirmação requerida" SEM
    card/proposta/botão (estado opaco v1 — decisão Sol (c) aprovada);
  - `validation_error`/`conflict`/401/429/5xx/rede/abort/protocolo →
    mensagem de erro estável por classe; `reasonCode`/`code` do enum pode
    acompanhá-la.
- `companyId`: `useSession().bootstrap?.companies[0]` (mock e real
  gateways; snapshot tem memberships ativas — é seleção de UX, NÃO
  autorização; o servidor revalida). Nome da empresa efetivo fica visível
  na UI e muda → reset (acima). Sem empresa → POST sem `companyId`.

### 2.3 UI (`agent-conversation.tsx`, `agent-dock.tsx`, `agent-page.tsx`, editados)

- Textos honestos (runner é single-turn — `AgentRunner.cs:65`): dock mostra
  o NOME da empresa enviada em vez de "Todas as empresas autorizadas ·
  Demonstração" (`agent-dock.tsx:37-39`); página dedicada troca "Conversa
  compartilhada…demonstração local" por aviso de envios independentes sem
  memória de turnos (`agent-page.tsx:28-30`); alerta vazio da conversa idem
  (`agent-conversation.tsx:83-87`).
- Remoção COMPLETA do mock de proposta (decisão Sol (e) aprovada): excluir
  `AgentProposal` de `agent-contract.ts`, `demoProposal`/estado `proposal`/
  import+render do card em `agent-conversation.tsx:12,95-97`, o arquivo
  `agent-confirmation-card.tsx` e os testes do botão demonstrativo em
  `agent.test.tsx:74-91` (substituídos pelos novos BDD).

### 2.4 Inventário (recalculado)

- **Novos (3):** `agent-client.ts`, `agent-client.test.ts`,
  `docs/verification/2026-08-23-agent-fe-wiring-test-cases.md`.
- **Editados (6):** `agent-conversation-provider.tsx`, `agent-contract.ts`,
  `agent-conversation.tsx`, `agent-dock.tsx`, `agent-page.tsx`,
  `agent.test.tsx`.
- **Excluídos (1):** `agent-confirmation-card.tsx`.
- Total 10 arquivos; estimativa ~550-650 linhas manuais com testes — dentro
  do teto de 800.

### 2.5 BDD mínimo (5 classes, mapeadas para teste/manifesto)

| Classe | Cenário | Evidência |
|---|---|---|
| sucesso | envio vira 1 POST com journey fixo + requestId único; `result` renderiza texto+fontes | `agent-client.test.ts` + `agent.test.tsx` |
| sucesso | sem empresa → POST sem `companyId`; `needs_clarification/company_required` vira mensagem fixa+reason | `agent-client.test.ts` |
| validação | `validation_error` tipado E 400 antiforgery `invalid_request`; `conflict` 409; corpo fora do schema → protocol | `agent-client.test.ts` |
| validação | 401/429/503/504/rede/abort → erro estável sem retry, sem body na UI | `agent-client.test.ts` |
| acesso | sem antiforgery token → falha tipada fixa; texto do servidor nunca vaza em erro | `agent-client.test.ts` |
| estado | reset/unmount/troca de empresa abortam e invalidam resposta tardia (epoch); `thinking` durante; trava de in-flight | `agent.test.tsx` |
| estado | `requires_confirmation` → mensagem fixa, sem card/botão | `agent.test.tsx` |
| concorrência | duplo clique rápido → exatamente 1 POST/1 requestId; envios sequenciais isolados | `agent.test.tsx` |

O manifesto `docs/verification/2026-08-23-agent-fe-wiring-test-cases.md`
seguirá o padrão da matriz da API (`issue-138-agent-api-test-cases.md:32-49`).

## 3. Fatia 2 — Imagens/figuras no contexto — DECOMPOSTA (abrir issues; NÃO executar)

Sol reprovou o bloco único; decomposição aprovada como issues separadas:

- **2A-Foundation (#134-A real):** implementação de `IAuthorizedDocumentSource`
  com ator/empresa CONFIÁVEIS na fronteira (hoje o contrato recebe só
  `fileRecordId` — `IAuthorizedDocumentSource.cs:16-24`; o desenho deve
  carregar `ActorContext`/`companyId` explícitos ou binding request-scoped
  equivalente), capability `files.read-ocr`, RLS, estado liberado, retenção/
  checksum, stream limitado/seekable, auditoria de decisão, revogação.
- **2B-Agent-BE (depende de 2A):** contrato estrito de `attachments` (refs
  opacas a documentos confirmados), limites (contagem/bytes), canonicalização
  (`ImageCanonicalizer`) + `DataContent` por bytes no perfil de visão
  aprovado, OpenAPI re-pinado. **Integridade:** a lista canônica de
  attachments ENTRA no hash de idempotência (`AgentExecutionApplicationService
  .cs:334-345` cobre hoje só jornada/mensagem/empresa) — mesmo `requestId`
  com attachments divergentes = 409 auditado, nunca replay do documento
  anterior. BDD de fronteira (empresa errada, membership revogada, teto de
  imagens, classe não consentida, tenant).
- **2C-FE (postponed):** client de upload (intention → storage → confirm),
  picker com preview, envio com refs. Só inicia após 2B estabilizar o
  contrato; condição de reavaliação registrada na issue.
- Pré-requisitos do dono para o ciclo completo: seed `ai.read`, config de
  provider texto+visão, engine de malware aprovado (hoje fail-closed) ou
  aceite do bloqueio.

## 4. Verificação e processo

- `npm test` em `apps/web` (vitest) + `./scripts/verify-public.sh` EXIT:0 no
  HEAD exato, SERIALIZADO (checar `pgrep -fl dotnet; docker stats` antes).
- Sol revisa o PR final (delta desta rev. 2 primeiro); Terra revisa o diff
  final antes do freeze se disponível.
- PR draft `codex/fe-chat-media-context` base main; evidência no comentário;
  issue #53 citada.

## 5. Execução isolada

Worktree próprio `.worktrees/fe-chat-media-context`; prefixo
`/tmp/vinx-agent-fe-` para evidências; não tocar ledger
`HANDOFF-mvp-builder.md` nem worktrees/PRs de outros agentes.
