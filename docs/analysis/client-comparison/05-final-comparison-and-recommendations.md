# Final comparison & recommendations — client's VINX vs our rebuild

Consensus of two independent analyses (mine in `04`, Sol's in
`sol-analysis.md`, extraction by Luna in `luna-capability-matrix.md`),
with Sol's evidence-backed corrections verified against pinned
`origin/main` (`bd0d5ab`) and `/tmp/vinx-client` (`2ccb0b4`).
Date: 2026-08-26.

## 1. Verdict

- The client's app **wins at operational discovery**: 102 tables / 33
  services / 59 DB functions encoding what a ~150-employee multi-company
  group actually needed in July 2026. It is a requirements document
  written in code — including live data we must migrate.
- Our rebuild **wins at trust**: BFF identity, per-company capabilities +
  RLS-via-GUC, exact money, append-only audit, outbox, file quarantine,
  tests. But its product surface is thinner than our own spec claims:
  Paguai is draft-only (approvals not wired), Atendimento is a fixed
  3-column board with raw membership-UUID assignment, notifications have
  no domain producers, MCP exposes only `vinx_ping`, PWA is target-only,
  and SMTP/Telegram live in open PRs, not on main.
- Neither is a complete ERP yet. The strategy: **adopt his behavior, not
  his architecture; complete our verticals before adding breadth; treat
  his data as a governed migration, not a copy.**

## 2. What his repo actually is (evidence, corrected counts)

Purchased Bootstrap admin template (`template-new`) + a hand-grown
Supabase core: browser talks to Postgres directly (anon key + RLS),
8 Edge Functions hold secrets (Serpro CNPJ OAuth2, user provisioning,
5-function IMAP/SMTP e-mail suite). 56 migrations, 233 registered routes
of which only 47 are service-backed; CRM funnel tables exist but were
never built into services. His best artifact is the automated-kanban
demandas workflow (DB-enforced routing/triggers/supervision) plus the
`0048–0054` sequence that made agenda/tarefas/demandas/chat/e-mail/feed
cross-company — evidence that employees need ONE personal work surface
across companies.

His own roadmap (17 releases) is disciplined on paper; execution skipped
its own R0/R1 (no tests, no audit, no staging). Real defects found in
review: HR wave tables (`0047`) readable by **all authenticated users**
with no `empresa_id` (verified: `for select to authenticated using
(true)`); `0018` deletes/reseeds all role grants; `0029` drops/recreates
`tarefas`; `0055` deletes all but one demand participant and its status
keys are mislabeled (`execucao`→"Em Iniciação"); direct `delete()` on
demandas; chat self-enrolment gap; bucket-wide storage policies.

## 3. Architecture comparison (what to keep from each)

| Dimension | His | Ours | Verdict |
|---|---|---|---|
| Shape | SPA → Supabase direct; RLS sole enforcement | SPA → BFF/API → modules → PG; RLS defense-in-depth | His is legitimate for low-risk CRUD (own-row preferences, advisory-locked codes, Serpro fn); unsafe for HR PII/files/payments/AI — keep BFF |
| Authorization | **global** cargo + module CRUD flags + global exceptions; `ve_todas_empresas` bypass | per-company memberships → capabilities/scopes; explicit company per command | NOT equivalent (his can't express different authority per company) — keep ours, adopt his 4-action grid only as admin UX for simple resources |
| Tenancy | `empresa_id`, later made **nullable** in agenda/chat/feed/demandas | company-owned rows; all-authorized is a derived read view | Keep our ownership; satisfy his cross-company need via read projections |
| Workflow rules | triggers/RPCs, incl. genuine single-flight locking | typed commands + version + idempotency + audit | Keep ours; preserve DB-enforced invariants for hot paths (already our rule) |
| Async | queue tables + browser-polled `SECURITY DEFINER` processor | transactional outbox + worker | Keep ours; time-based demand transitions must be worker-owned, not browser-cron |
| Money | numeric but JS `number` mid-layer | C# decimal + decimal-string JSON + canonical hash | Keep ours |
| Files | 6 buckets, bucket-wide policies | signed intent + quarantine + malware scan + audit | Keep ours |
| E-mail | IMAP/SMTP suite, creds in Vault, no durable scheduler | none (SMTP PR open) | Do not port; outbound adapter + provider integration only |

## 4. Feature disposition

**Adopt (ordered, each WITH its data migration):**
1. **Demandas workflow** (his crown jewel) — bounded module, NOT a generic
   kanban engine: company-owned items, typed transitions, version checks,
   supervisor/watchers, 1–2 validated rotinas, checklists, effective
   deadlines, worker-owned time triggers, outbox notifications, Foundation
   Approvals for `Em Aprovação` (his any-participant-completes is a
   segregation hole). Lateness = derived projection, not a stage. Field
   edits: re-initiate only on responsibility/routine change, not title
   fixes. Honest status names (his keys are mislabeled).
2. **Customers (PF/PJ) + Serpro CNPJ** — neutral Party/CRM bounded context
   (not an extension of employee `people`): transactional multi-child
   writes (he does browser delete-and-reinsert), import preview, dedupe,
   soft delete, provider adapter with provenance/cache/quota/audit.
3. **Meu Trabalho** (not "Meu Painel + Agenda") — one server-owned
   all-authorized read model: my due/overdue work, decisions awaiting me,
   unread alerts, company badges, deep links. The `0048–0054` migration
   cluster is the strongest usage evidence in the repo. Calendar defers
   (recurrence/participants/reminders are not cheap).
4. **People Core correction** (prereq for any HR) — separate
   person/employment from login/membership; support employees without
   accounts (his model has them; ours requires e-mail); employer company,
   job/unit/team/manager; import reconciliation.
5. **One HR vertical, chosen by measured use** (document expiry/admission,
   leave/absence, or offboarding — not payroll/time/performance breadth).
   Sensitive unsupported records → restricted archive, never generic CRUD.
6. Projects/tasks/calendar — only if activity readback justifies; projects
   are their own aggregate, **not** a kanban grouping.

**Improve what we already have (his evidence):**
- **Paguai**: first fix the verified integrity gap — proof `fileId` is
  never persisted with the create (`paguai-page.tsx:150,367`,
  `PaguaiDraftContracts.cs` has no proof-file field); then wire
  submit→approval→payment slices + due/overdue counters; wire notification
  intents (producers missing today).
- **Atendimento**: company-scoped people picker (no raw membership UUIDs),
  requester/customer/priority/channel/SLA fields, watchers, history —
  keep it separate from Demandas.
- **Home**: replace module tiles with Meu Trabalho projection.
- **Agent/AI**: narrow tasks only (summarize authorized item, extract
  document candidate, suggest rotina, draft reply).

## 5. The part nobody planned: data migration is a first-class feature

102 live tables with mutated ownership/identity semantics cannot be
lift-and-shifted. Program (from Sol, adopted): (1) immutable production
snapshot (schema+RLP+functions, applied-migration ledger, row counts,
Auth/profile/employee export, bucket manifests, queue states) — this also
settles the Demandas-vs-Customers order by real activity; (2)
classification/ownership/identity/status mapping ledgers; (3) target
vertical contract BEFORE import; (4) idempotent server-side importer
(staging schema, deterministic mapping version, receipts,
company-by-company transactions); (5) files through quarantine + two
rehearsals + short write-freeze cutover + read-only legacy archive. Never
dual-write; CDC only if downtime constraints demand it.

## 6. Ideas beyond his feature list (leverage he could never build)

1. **WhatsApp intake → typed candidate → human-confirmed command**
   (invoice/purchase/document photo → OCR → draft Paguai/demanda with
   evidence attached). Uses #140/#272 inbound + OCR #134 + guardrails.
2. **Meu Trabalho morning briefing** (deterministic list first; AI
   summarizes only authorized items, cites record IDs, never transitions).
3. **Document compliance/expiry workbench** (contracts, certificates,
   employee docs) — OCR extracts dates as candidates; Demandas get the
   review task.
4. **Process intelligence** (cycle time by stage, rework, queue aging,
   approval bottlenecks from history/outbox) — small metric dictionary, no
   report builder, no prompt/data leakage.
5. **Job-oriented MCP tools** after trusted ActorContext (#132/#133):
   `list_my_due_work`, `explain_paguai_request`, then prepare-only
   commands. No generic SQL tools.
6. **Assisted customer onboarding** (CNPJ → Serpro + OCR + dedupe +
   import preview in one transactional create).

## 7. Explicit do-NOT-build list

Full IMAP/SMTP e-mail client (integrate a provider; outbound adapter
only); internal chat/WebRTC (collaborate-suite integration or nothing);
corporate feed (targeted announcements w/ acknowledgement instead);
native mobile before PWA is real and telemetry proves field-offline need;
the 21-table HR suite as CRUD; generic workflow engine (until 3 stable
workflows share semantics); generic report builder; payroll/fiscal/bank
execution/e-signature (providers, per his own roadmap's best lines);
autonomous AI approvals/payments (prepare/confirm stays).

## 8. Top-10 actions

**Do now:** 1) production evidence snapshot + migration ledgers +
importer skeleton; 2) fix Paguai proof-file persistence + emit one real
notification intent end-to-end; 3) Atendimento human-usable slice (people
picker, priority/SLA/history); 4) Demandas vertical #1 (1–2 rotinas) with
data migration; 5) Customers + Serpro vertical with migration; 6) Meu
Trabalho read model.
**Later:** 7) People Core correction + employee directory migration;
8) one HR vertical by measured use; 9) first production-grade read-only
MCP tool; 10) WhatsApp→OCR intake for one document type.

## 9. Corrections log (pre-review errors in 04, kept for the record)

58→56 migrations; 34→33 services; multiempresa semantics not equivalent;
money handling not equivalent; client async "none" → queue tables exist,
dispatcher missing; SMTP/Telegram/PWA/approvals/agent-FE are PRs or
targets, not pinned-main reality; "RH full HRIS" → generic CRUD with
unused `rh_alcadas` and unsafe reads; "agenda cheap" unsupported;
"projects as kanban grouping" wrong; "DB refuses illegal moves" → UI
refuses, DB mostly doesn't (`for all` participant policies).
