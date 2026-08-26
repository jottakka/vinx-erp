# Interface profissional multiempresa — plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Corrigir os fluxos enganosos, permitir leitura agregada das empresas autorizadas e entregar uma interface azul, profissional e consistente para Paguai, Atendimento, Pessoas e Agente.

**Architecture:** Leituras podem agregar somente as empresas para as quais o bootstrap e o servidor concedem a capability aplicável; cada mutação continua vinculada a uma empresa explícita. MUI Core permanece como biblioteca visual, com três componentes compartilhados pequenos. Mudanças de integridade no backend preservam RLS, idempotência, auditoria e arquivos privados.

**Tech Stack:** React 19, TypeScript, MUI 9, React Hook Form, Zod, ASP.NET Core 10, EF Core, PostgreSQL, Vitest e xUnit.

**Issue:** [#315](https://github.com/jottakka/vinx-erp/issues/315)

## Global Constraints

- Uma única branch `codex/professional-multicompany-ui`; a autorização explícita do responsável cobre a exceção aos limites de 25 arquivos/800 linhas e evita PRs artificiais.
- O marrom sai do tema inteiro; base azul/navy e superfícies neutras, com contraste AA preservado.
- Cor nunca é o único sinal: empresa e status sempre têm texto/monograma ou ícone.
- “Todas as empresas” é somente leitura agregada. Criar, mover, cancelar, anexar e executar o agente exigem uma empresa explícita.
- Não adicionar dependência visual enquanto MUI Core atender o caso real.
- Comentário novo só explica garantia não óbvia de segurança, transação, concorrência ou regra de negócio.
- Testes focados em RED/GREEN; uma verificação web afetada no fechamento e integração backend somente dos contratos alterados. CI continua como gate amplo.
- Não acessar `quality/private`.

## Estimativa e decomposição interna

- Tema e componentes compartilhados: 7–10 arquivos não teste; 250–450 linhas manuais.
- Paguai: 12–18 arquivos não teste; 500–850 linhas manuais, mais migration/snapshot gerados.
- Pessoas e Agente: 8–12 arquivos não teste; 300–550 linhas manuais.
- Atendimento: 16–24 arquivos não teste; 700–1.100 linhas manuais, mais migration/snapshot gerados.
- A entrega excede os limites usuais por reunir subsistemas independentes; o responsável pediu explicitamente uma implementação coordenada, paralela e sem múltiplos PRs. Os commits e as tarefas abaixo continuam separados para revisão.

---

### Task 1: Tema azul e controles multiempresa reutilizáveis

**Files:**
- Modify: `apps/web/src/app/theme/palette-primitives.ts`
- Modify: `apps/web/src/app/theme/create-vinx-theme.ts`
- Modify: `apps/web/src/app/theme/surface-tokens.ts`
- Create: `apps/web/src/shared/ui/company-identity.tsx`
- Create: `apps/web/src/shared/ui/authorized-company-filter.tsx`
- Create: `apps/web/src/shared/ui/status-chip.tsx`
- Modify: `apps/web/src/shared/ui/company-select.tsx`
- Modify/Test: theme and shared UI test files beside the components

**Interfaces:**
- Produces `AuthorizedCompanyFilter` with `companies`, `selectedCompanyIds`, `onChange`, `label`, `disabled` and explicit empty selection meaning all authorized companies.
- Produces `CompanyIdentity` rendering a stable company monogram/accent without exposing IDs.
- Produces `StatusChip` receiving explicit `label`, `tone` and optional icon; feature modules own their status mapping.

- [ ] Write focused tests proving multi-select/all-authorized behavior, accessible labels, company identity and semantic status output; run and observe RED.
- [ ] Replace warm palette, hard-coded warm field fills and brown shadows with semantic blue/slate values while preserving light/dark contrast.
- [ ] Implement the three focused shared components using MUI Core only.
- [ ] Re-run the focused theme/shared tests and the existing theme test; commit.

### Task 2: Paguai honesto, persistente e multiempresa

**Files:**
- Modify: `PaguaiRequest`, EF configuration, Paguai contracts/service/controller/request model
- Create: one EF migration adding nullable `invoice_file_id` linked to `foundation.files`; update generated snapshot
- Modify: `apps/web/src/features/paguai/*` production files and focused tests
- Modify: `docs/modules/paguai.md` and the existing Paguai verification manifest only where the durable contract changes

**Interfaces:**
- `CreateDraftCommand` and `PaguaiDraft` gain `Guid? InvoiceFileId`.
- JSON create/read contracts gain `invoiceFileId`.
- The server accepts only a live, confirmed, same-company file classified as `paguai_request_invoice`; include its ID in the canonical idempotency hash.
- The UI uploads “Fatura ou documento da solicitação”, persists the ID, reads it back after refresh and keeps `proofRequirement` as the later payment-proof rule.

- [ ] Write focused frontend and backend tests for date validity/label, persistent invoice readback, missing/foreign/unconfirmed/wrong-classification file, and idempotency conflict; observe RED.
- [ ] Implement the contract, migration and same-company file validation without adding a public URL.
- [ ] Replace the single-company gate with `AuthorizedCompanyFilter`, fan out existing authorized per-company reads, merge deterministically and show `CompanyIdentity` on every card.
- [ ] Render a professional Paguai board from actual returned states only; map labels/colors and never let drag-and-drop mutate financial state.
- [ ] Harden attachment failure/removal state, pending submit state, Portuguese validation and the native date label.
- [ ] Run only the focused Paguai web tests, focused backend tests, migration/model guard required by the changed schema, and commit.

### Task 3: Pessoas e Agente em linguagem humana

**Files:**
- Modify: `apps/web/src/features/people/*`
- Modify: `apps/web/src/features/agent/*`
- Modify focused People/Agent tests and compact frontend verification manifest if behavior mapping changes

**Interfaces:**
- Pessoas defaults to all authorized companies, supports selected-company chips and local name search over authorized results.
- Employee rows/dialogs show name and company identity; normal UI never renders `employeeId` or `membershipId`.
- The create operation is labelled “Cadastrar funcionário”; it remains explicit that no invitation/access is sent.
- Agent receives an explicit company selector for each conversation; changing company clears/aborts stale conversation and attachment state.

- [ ] Write focused tests for all/selected company results, name filtering, no UUID leakage, clear creation wording and Agent company reset; observe RED.
- [ ] Move employee creation into a focused dialog and make the directory/search the primary surface.
- [ ] Reuse `AuthorizedCompanyFilter`, `CompanyIdentity`, skeletons and retry actions; do not create a generic form framework.
- [ ] Add the Agent company selector without allowing “all companies” for a company-scoped send.
- [ ] Run focused People/Agent tests and commit.

### Task 4: Atendimento como fila real, não alias de Kanban

**Files:**
- Modify: Atendimento domain/configuration/contracts/service/controller/request models
- Create: cancellation request/contract and one EF migration for cancellation plus a nullable attachment reference
- Modify: `apps/web/src/features/atendimento/*`
- Modify: route/navigation manifest so `/kanban` no longer renders `AtendimentoPage`
- Modify focused Atendimento integration/web tests and `docs/modules/atendimento.md`

**Interfaces:**
- Dedicated cancel command: `CompanyId`, `RequestId`, `ExpectedVersion`, trimmed `Reason`; cancellation is terminal, audited and visible as `cancelado`.
- Create card optionally accepts one confirmed same-company `service_desk_attachment` file.
- Assignment options endpoint returns authorized same-company `{ membershipId, displayName }`; UI displays names and keeps IDs opaque.
- Multiple selected people are persisted as involved participants. In-app notification delivery is implemented only if it can reuse the existing transactional notification primitive without a second transaction; otherwise participants ship without a false “notified” claim.

- [ ] Write focused backend tests for cancel success/terminal conflict/replay/foreign tenant and attachment validation; write web tests for all-company board, colors, person picker and no membership text; observe RED.
- [ ] Implement dedicated cancellation and attachment persistence with RLS/idempotency/audit preserved.
- [ ] Implement assignment options and participant persistence; do not model RH/Contabilidade groups until a real employee-to-team relationship exists.
- [ ] Build the four-column professional board (`Novos`, `Em andamento`, `Concluídos`, `Cancelados`) with company identity on every mixed-company card.
- [ ] Remove the duplicate Kanban alias from product navigation/route behavior; do not invent an unauthorised in-memory demand domain.
- [ ] Run focused Atendimento web and backend tests plus required migration/model guard; commit.

### Task 5: Correção consolidada e verificação proporcional

**Files:**
- Modify only files required by validated review findings
- Update one compact verification manifest for the coordinated delivery

**Interfaces:**
- Sol reviews security/integrity paths, professional visual consistency and comment noise once over the consolidated branch.

- [ ] Generate a branch review package and ask Sol for one complete critical review.
- [ ] Send all validated findings in one correction round to one implementer; run one scoped re-review.
- [ ] Run web lint, format, affected Vitest files, build and no-direct-Supabase once after scope freeze.
- [ ] Run affected backend build/tests and database guards only for changed contracts; do not run the complete local public gate unless a strategic trigger appears.
- [ ] Recalculate files/manual lines/generated artifacts and record exact evidence in the final handoff/PR body.
