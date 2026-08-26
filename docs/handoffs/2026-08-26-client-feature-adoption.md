# Handoff: client-feature adoption program (post-comparison)

Date: 2026-08-26. Sources: comparison in
`docs/analysis/client-comparison/` (read `05-final-comparison-and-recommendations.md`
first; Sol's `sol-analysis.md` has the deepest per-item evidence). Client
repo reference clone: `/tmp/vinx-client` (re-clone
`https://github.com/cbqoliveira/vinx-erp` if gone; head `2ccb0b4`). Our
tree facts were pinned at `origin/main` `bd0d5ab` — re-verify before
acting; PRs #306 (SMTP) and the Telegram stack may have merged since.

How to read "His UI" sections below: **inspiration, not target.** The
client bought a Bootstrap admin template; his screens are Bootstrap 5 +
Tabler icons + jQuery-era modals, three UI kits deep. We do MUI + our
design tokens. Copy the *interaction decisions that encode real
operational needs*; never copy markup, CSS, or the demo surface.

## Global invariants (apply to every item)

1. Commands: explicit company, named capability, idempotency key,
   optimistic `version`, append-only audit, transactional outbox event.
   Endpoints/MCP tools stay thin transports.
2. RLS stays defense-in-depth via the GUC context; every new table gets
   policies + integration tests (cross-company denial, inactive user,
   capability gates).
3. Soft delete (`deleted_at/by/reason`) for business entities; no raw
   `DELETE` paths in services (his `demandasService.excluir` is the
   anti-pattern).
4. Every adopted slice ships WITH its legacy data migration (see
   `05` §5 program: snapshot → ledgers → target contract → idempotent
   server-side importer → rehearsal/cutover). No dual writes.
5. Definition of done per architecture spec: authorization, audit,
   observability, public tests, MCP-tool decision documented.
6. Work order: 0 (Paguai fix) → 1 (Atendimento slice) → 2 (Demandas) →
   3 (Customers) → 4 (Meu Trabalho) → 5 (People Core) → 6 (HR vertical).
   Items 2/3 may swap after production activity readback.

---

## Item 0 — Paguai proof-file integrity fix (pre-requisite, small)

**Problem (verified at `bd0d5ab`).** The create/update dialog performs a
real private upload and receives `result.proof.fileId`
(`apps/web/src/features/paguai/paguai-page.tsx:150`), but the id is kept
only in React state; the payload sent to `POST /paguai/requests` has no
proof-file field (`src/Modules/Vinx.Erp.Paguai/PaguaiDraftContracts.cs`
defines only `ProofRequirement` strings). UI copy admits it: "Comprovante
anexado nesta sessão" (`paguai-page.tsx:367`). Reload ⇒ request shows no
proof; storage accumulates orphans.

**Build:**
1. Add `ProofFileId` (nullable Guid) to the create/update request
   contract and to the draft/read contracts. Server validates: file
   exists, owned by actor, same company, past quarantine/scanning, not
   already bound to another request.
2. Persist the relation on the draft row (or a `paguai.request_files`
   child table if multiple proofs are expected — decide once; his UX
   attaches one comprovante, keep one column for now).
3. Read path returns it; page renders the attached proof after reload
   (signed short-lived URL, fresh authorization per download — no
   durable URLs).
4. Update/replace flow: uploading a new proof rebinds and audit-records
   the swap; old file retention follows file policy.
5. Tests: integration — create with proof, read back after "reload"
   (new query), cross-company file rejection, already-bound rejection.
   Vitest — dialog state and error surfaces.
6. While there (same PR or an immediate follow-up): emit one real
   notification intent (draft created → notify approver/watchers) so the
   outbox/worker path finally has a producer.

**His UI inspiration:** his "Documentos & LGPD" tab buffers attachments
before first save and switches to immediate upload after the record
exists — the buffering idea is good UX for create-flows; our version
should keep the upload-before-create pattern we already have, but close
the loop by persisting the binding server-side.

---

## Item 1 — Atendimento operational slice (improve what we have)

**Gap.** Our board: title, description, assignee (raw membership UUID
typed by hand), three fixed columns. His `tickets` (migration `0029`):
requester, customer, channel, priority, SLA deadline, timestamps,
history.

**Build (smallest honest slice):**
1. Company-scoped people picker for assignee (query
   `EmployeeDirectoryService`; display name + initials avatar; no UUIDs
   in UI). Requester field (person or customer once Item 3 exists).
2. Fields: priority (Crítica/Alta/Média/Baixa), category, channel
   (whatsapp/email/web/manual), SLA due date; overdue derived badge.
3. Watchers; visible per-ticket history (append-only events we already
   audit — surface them).
4. Filters: status/priority/assignee/overdue (server-side, paginated).
5. Keep 3 columns; do NOT build a board engine — that's Item 2's cousin,
   not this.

**His UI inspiration (tickets.tsx, 469 lines):** filter bar with
busca + empresa + prioridade + responsável + status passed server-side
into the list query (good: filters are query params, shareable);
status/priority rendered as colored chips from one lookup table each
(good: single source of labels/colors — do it with our design tokens);
detail via modal with sectioned form. Do better: his channels/priorities
are hardcoded component constants — make ours reference/lookup data with
capability-gated editing, and give the detail view a real activity
timeline instead of his flat history table.

---

## Item 2 — Demandas module (the crown jewel, bounded)

**What it is (his spec: `docs/FLUXO_KANBAN_AUTOMATIZADO.md`; impl:
`0055` + `demandasService.ts` + `produtividade/kanban.tsx`).** Internal
request workflow: every demand is born with a responsible; routing
prefers rotina by tipo+departamento, then either, then wildcard, then
default; supervisor + watchers get notified; stages advance by exactly
one trigger per stage — explicit completion, checklist-100%, or elapsed
time; no free card movement; overdue is tracked; field edits re-initiate.

**Build (ours):**
1. `src/Modules/Vinx.Erp.Demandas/` (Domain/Application/Infrastructure/
   Api/Mcp). Tables in `demandas` schema: `demands`,
   `demand_routines` (+ `stage_rules`), `demand_participants`,
   `demand_checklist_items`, `demand_history` (or reuse audit +
   projection), notifications via existing intent/outbox.
2. State machine in the Domain layer, not triggers: typed commands
   `CreateDemand`, `AdvanceStage`, `CompleteChecklistItem`,
   `ReassignResponsible`, `PauseAutomation`, `ResumeOverdue`. Each
   validates legal transitions; DB keeps single-flight locks on the
   demand row for hot paths (our "precise SQL for critical commands"
   rule).
3. Time triggers belong to the Worker (scheduled scan of due stage
   rules), never browser polling. Idempotent by (demand, stage,
   trigger-key).
4. Approvals: `Em Aprovação` must consume Foundation Approvals with
   prepare/confirm — his any-participant-completes is a segregation
   hole; keep eligibility server-side.
5. Overdue = derived projection (`due_at` passed, not closed) + optional
   "Atrasos" saved view — NOT a mutual-exclusive stage (his model loses
   "overdue while awaiting approval").
6. Field-edit re-initiation: only responsibility/routine/type/
   department changes re-initiate; title/description edits do not.
7. Routines are company-scoped, versioned config (his are global with
   first-admin default owner; `ordem_roteamento` precedence kept).
8. Status vocabulary: honest names (`initiation`, `execution`,
   `awaiting_approval`, `closed`) — his keys are mislabeled
   (`execucao` renders "Em Iniciação").
9. Honest `data-migration` note: `0055` deleted all-but-one participant
   and remapped statuses/priorities; migrate from the production
   snapshot readback, mapping ledger per `05` §5, never from repo files.

**His UI inspiration (kanban.tsx, 1199 lines):**
- No drag-and-drop at all — advancement is an explicit "Concluir etapa"
  button per card + checklist ticks; the UI *shows* the process. Keep
  this discipline; add drag only for reordering within a stage if ever
  asked.
- Card metadata: priority chip, responsible avatar, `prazoEfetivo` =
  min(prazo, SLA, stage deadline) with per-card countdown, and
  "tempo na etapa" (hours in current stage). Both are cheap, high-signal
  — adopt.
- Deep-link `?demanda=<id>` opens the card (query-param state, not
  ephemeral modal state) — adopt for shareability.
- Rotinas managed in a separate admin modal (nome, tipo, departamento,
  ordem de roteamento, checklist padrão as newline list, per-stage
  trigger select). Do better: a real admin screen with capability
  gating, versioned diffs, and a dry-run preview ("a demand with
  tipo X + depto Y would route to…").
- Filters: busca + prioridade + responsável (client-side memo — do ours
  server-side, paginated).
- Supervisor permission check queried per-card
  (`demanda_pode_trocar_responsavel`) — we resolve capabilities in the
  API response instead.

---

## Item 3 — Customers (PF/PJ) + Serpro enrichment

**What he has:** rich registry (migration chain `0002`–`0013`, `0034`;
service `clientesService.ts`; UI `vinx/clientes.tsx`, 1529 lines):
PF/PJ, multiple addresses, contacts, PJ representatives (sócios),
documents + LGPD consent + dedupe (unique CPF/CNPJ per company,
advisory-locked sequential client code), economic groups, Serpro CNPJ
consultation via Edge Function.

**Build (ours):**
1. Bounded context decision: new `Vinx.Erp.Customers` module with a
   `Party`/customer aggregate — NOT an extension of employee `people`
   (different lifecycle, privacy profile, and access matrix).
2. `ReceitaGateway` provider adapter (like `WhatsAppGateway`): Serpro
   Consulta CNPJ behind OAuth2; capability-gated; per-call quota/cache
   with freshness timestamp (`cnpj_consultado_em`), timeout, audit;
   provenance-stamped fields. Plus `buscarCep` adapter (he uses a CEP
   lookup for addresses) — same pattern.
3. Transactional multi-child create/update: customer + addresses +
   contacts + representatives commit as ONE command with version —
   his UI orchestrates delete-and-reinsert per child from the browser.
4. Import: spreadsheet upload → staging → preview with per-row
   validation → confirm (his roadmap requires it; he never built it).
5. Dedupe on create/import; soft delete; sequential per-company code
   via our locking function pattern.
6. Migration: company-by-company, same-company FK enforcement for
   children, quarantine unresolved duplicates/cross-company links.

**His UI inspiration (clientes.tsx):** the "Consultar CNPJ" button is
the model to steal: 14-digit input → one click → form fills razão
social, fantasia, abertura, situação, regime sugerido, capital, porte,
CNAE, contact data; sede address merged into the existing address list
(Sede slot first, else first empty, else prepended); sócios become
candidate representatives; **fill-only-empty merge**
(`f.x.trim() ? f.x : d.x` — never overwrites what the user typed);
Receita-sourced fields then lock with a "Preenchido pela consulta de
CNPJ" hint and a consulted-at timestamp. Do better: show provenance
per field with stale-freshness re-consult affordance, and make the
enrichment an explicit reviewable diff before commit. His tabbed modal
(Dados / Endereços / Contatos / Representantes / Documentos & LGPD)
with per-tab counts is a good information architecture for a dense
record — reproduce the structure in our dialog system, not the markup.
LGPD consent capture inline in the Documents tab (consent flag +
timestamp) is legally sensible — adopt with audit.

---

## Item 4 — Meu Trabalho (cross-company action inbox)

**Evidence:** his migrations `0048`–`0054` systematically detached
agenda/tarefas/demandas/chat/e-mail/feed/projects from the
active-company selector — employees live across companies and need ONE
personal work surface. His implementation: browser fan-out of 9 queries
(`dashboardColaboradorService`) with per-source failure isolation.

**Build (ours):** one server-owned read model —
`GET /my-work?within=all-authorized` (the read-only group view our
authorization model already defines): items requiring my action (my
open demands next-step, approvals awaiting me, assigned atendimento
tickets, Paguai drafts pending my role), due/overdue with company
badge, unread notifications, safe deep links per item. Deterministic
projection first; TanStack Query with sane staleTime; NO polling of 9
sources; per-section error states. Preferences (ordering, visible
sections) later, stored like his `painel_usuario` (own-row, RLS-self).

**His UI inspiration (dashboard/dashboard/index.tsx):** hero with
greeting + date + session-stable motivational phrase; progress ring
("X% concluído" across tasks+cards — his mixes stale models, we compute
server-side); 8 metric cards each deep-linking to its source with
"Itens atrasados" as an aggregated red card routing to whichever source
has overdue items (good triage affordance); per-section lists with
consistent empty states (`EmptyState` icon+title+detail — adopt, our
design tokens); 60s `refetchInterval` + manual "Atualizar painel" (do
better: server-driven freshness or notification-triggered
invalidation, not fixed polling). Skip: feed integration,
motivational-phrase catalog (taste call, cheap to add later), chat
unread counts (no chat in ours — notifications cover it).

---

## Item 5 — People Core correction (prerequisite for HR)

**Gap (ours):** `Employee` is tied to a company membership and the
create path requires an e-mail (creates membership). His model:
employees exist without ERP access (his RH painel even shows a
"sem acesso ao ERP" list as an operational indicator); 1:1 optional
link funcionário↔perfil (migration `0058`).

**Build:**
1. Split the model: `Person` (natural person, PII-minimal) →
   `Employment` (company, job title, unit/team, manager, start/end,
   status) → `UserAccount`/membership stays the access boundary. An
   employee can have zero or one account; an account can hold
   employments in several companies (matches our membership model).
2. Capabilities split: directory read vs create/admin; sensitive
   attributes (salary, bank, documents) least-privilege and
   audit-guarded, medically-sensitive content segregated per spec.
3. Import reconciliation flow for the legacy `funcionarios` table
   (his `0036` import fields): spreadsheet → staging → preview diffs →
   confirm; CPF normalization + dedupe (his `0058` migration warns:
   duplicates break the user-link unique constraint — handle in
   staging, not on constraint violation).
4. Controlled account invitation from an employment (invite creates
   membership in that company only).

**His UI inspiration (rh/colaboradores.tsx, 822 lines):** tabbed
employee form — Dados pessoais / Endereço / Contratação / Acesso —
with CPF mask+validation, CEP autocomplete on address, BRL-masked
salary, schooling/marital/bank selects, UF list, employment dates;
avatar with deterministic color from id (nice touch, adopt);
"Com acesso ao ERP" column + painel card surfacing who lacks access
(adopt — it operationalizes the person≠account split). His RH painel:
4 indicator cards (total/ativos/inativos/com acesso) + recent hires +
sem-acesso list — trivially cheap, useful; reproduce on our people
landing. Do better: his page holds ~30 hardcoded select-option arrays
(escolaridades, nacionalidades…) — ours become reference tables with
capability-gated editing.

---

## Item 6 — ONE HR vertical (chosen by measured use)

**Rule:** pick exactly one after production readback shows real
activity + the owner names a data owner + retention is decided.
Best candidates (Sol's ranking): document expiry/admission checklist,
leave/absence (férias/ausências), offboarding. NOT payroll, NOT time
clock, NOT performance breadth.

**Build:** vertical slice with statuses as typed transitions, approval
via Foundation Approvals where signatures matter, notifications via
outbox, sensitive fields behind dedicated capabilities, documents
through our file quarantine. `rh_alcadas` (his approval thresholds
table, `0046`) maps onto our Foundation approval policies — configure
there, don't clone the table.

**His UI inspiration:** each HR vertical is a simple
`ListaCadastro.tsx`-style list + tabbed form (ferias.tsx 232 lines,
desenvolvimento.tsx 316…) — honest CRUD, low ceremony. The lesson is
scoping, not design: small focused screens per lifecycle event beat a
monolithic "HR module" page. His known defect to avoid: the wave-B–E
tables are readable by every authenticated user with no company
column — our version: company-scoped employment FK + capability-gated
reads from day one, plus restricted archive for whatever we don't
rebuild (legacy rows stay retrievable under audit, never exposed via
generic CRUD).

---

## Explicitly out of scope for this program

IMAP/SMTP e-mail client; internal chat/WebRTC; corporate feed;
chamadas registry; native mobile; generic workflow engine; generic BI/
report builder; payroll/fiscal/bank execution; autonomous AI actions.
(Reasoning in `05` §7.) WhatsApp intake → OCR → typed draft is a
separate initiative that REUSES Items 2/3 outputs — do not bundle.

## Suggested issue breakdown (owner's call on numbers)

1. Paguai proof-file persistence + read-back + notification producer
   (Item 0) — small, do first.
2. Atendimento: people picker + ticket fields + filters (Item 1).
3. Demandas: domain + commands + worker triggers + UI + migration
   (Item 2) — biggest; split FE/BE if needed.
4. Customers: aggregate + ReceitaGateway + import + migration (Item 3).
5. My Work read model + home replacement (Item 4).
6. People Core model split + import reconciliation (Item 5).
7. HR vertical TBD by readback (Item 6).
Parallel-safe: 0–1 anytime; 2–3 sequential or split FE/BE; 4 anytime
after 1; 5 before 6.
