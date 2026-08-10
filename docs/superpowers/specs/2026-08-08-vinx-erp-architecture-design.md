# Vinx ERP Architecture Design

## Status

Approved architecture baseline. This document is the canonical, versioned
record of the decisions made before project scaffolding. Product-flow details
continue in their respective GitHub issues.

## Goals and constraints

- Internal, multi-company ERP for approximately 150 employees.
- Preserve the original React/Vite/Supabase project as reference; start the
  new repository clean.
- Deliver quickly with a maintainable modular monolith, primarily maintained
  by an experienced C# developer.
- Start with Supabase but avoid provider coupling at application boundaries.
- Treat MCP, authorization, auditability, observability and tests as product
  requirements rather than integrations added later.
- Prefer SOLID, DRY and KISS in their practical sense; do not introduce
  abstractions without a current, concrete reason.

## Chosen stack

| Concern | Decision |
|---|---|
| Frontend | React 19, TypeScript, Vite, React Router, TanStack Query, React Hook Form, Zod, MUI, TanStack Table/Virtual, Vitest, Testing Library, MSW and Playwright. V1 funciona somente online; não entrega PWA nem cache offline. |
| Backend | .NET 10 LTS, ASP.NET Core Minimal APIs grouped by module, EF Core with Npgsql and explicit validation. |
| Database | Managed Supabase PostgreSQL initially, using standard PostgreSQL schemas and migrations. |
| Identity | Supabase Auth behind the ASP.NET Core BFF; e-mail/password plus real TOTP initially; corporate OIDC/SAML later through an adapter. |
| Files | Private Supabase Storage through a `FileStorage` boundary; signed temporary upload/download URLs, quarantine and malware scanning. |
| Async work | PostgreSQL transactional outbox plus a separate .NET worker; no broker initially. |
| MCP | Separate ASP.NET Core MCP Host using the same application modules as the API and OAuth 2.1 for remote access. |
| Containers | Docker for every host and environment. Docker Compose is mandatory locally. |
| Telemetry | OpenTelemetry, structured logs, health checks and a portable exporter configuration. |
| CI | GitHub Actions within the free allowance, with a zero spending cap; heavy runs are manual or pre-homologation. |

## Hosts and module boundaries

The solution has three entry hosts, not microservices:

```text
React web -> Api.Host -> Modules -> PostgreSQL / provider adapters
MCP client -> Mcp.Host -> Modules -> PostgreSQL / provider adapters
Workers    -> Worker.Host -> Modules -> PostgreSQL / provider adapters
```

`Api.Host`, `Mcp.Host` and `Worker.Host` reference the same module application
services. Controllers/endpoints and MCP tools remain thin transports. Business
rules do not live in endpoints, tool handlers or workers.

Initial modules are `Foundation`, `Paguai`, `ServiceDesk`, `Kanban`, `HR`,
`Notifications` and `AI`. Foundation owns cross-cutting capabilities, but it
does not become a dumping ground for business rules.

Suggested initial structure:

```text
apps/
  web/
  api-host/
  mcp-host/
  worker-host/
src/
  Foundation/
    Audit/
    Authorization/
    Files/
    Identity/
    Observability/
  Modules/
    Paguai/{Domain,Application,Infrastructure,Api,Mcp}/
    ServiceDesk/{Domain,Application,Infrastructure,Api,Mcp}/
    Kanban/{Domain,Application,Infrastructure,Api,Mcp}/
    HR/{Domain,Application,Infrastructure,Api,Mcp}/
    Notifications/{Domain,Application,Infrastructure,Api,Mcp}/
tests/
  public/
quality/
  private/
docs/
  adr/
  modules/
  runbooks/
  superpowers/{specs,plans}/
```

Do not add generic repositories, base services, mandatory mediator/CQRS,
AutoMapper, Dapper, Redis, Kafka, Kubernetes or microservices during
groundwork. Add an interface only for an actual provider boundary, alternate
implementation or independently testable volatile dependency.

### Operação online-only do frontend

A V1 não registra respostas autenticadas, comandos pendentes ou dados de
domínio para uso offline. Não há service worker, fila de escrita ou reenvio
automático após reconexão. A interface pode indicar indisponibilidade usando
os sinais do navegador e falhas confirmadas da API; ao recuperar conexão, ela
revalida dados autorizados antes de atualizar a tela.

## Multi-company authorization

- A user has explicit active memberships and independently assigned roles in
  each company.
- Roles grant named capabilities, such as `payment.approve` and
  `medical_certificate.validate`, scoped to self, team, unit, company or
  group.
- The active context is one company or the read-only `all-authorized` view.
  Every command requires one explicit company.
- The API returns effective capabilities for the active context. The frontend
  uses them to hide menus, routes, controls, columns and data but never makes
  the security decision.
- Switching company clears only in-memory query and capability state before
  displaying the new context; this state is never persisted as offline cache.
  Deep links to forbidden routes deny before loading data.
- API, MCP and database each check the applicable boundary. PostgreSQL RLS
  isolates tenants; database commands independently protect high-risk actions
  such as payment approval/confirmation, medical document access and exports.

## Data model principles

- One PostgreSQL database with logical schemas: `foundation`, `paguai`,
  `service_desk`, `kanban`, `hr`, `notifications` and `audit`.
- Business data has an explicit `company_id`; a group-wide report is an
  authorized union, never a tenant-less row.
- Foundation includes groups, companies, units, teams, users, memberships,
  roles, capabilities, role assignments, employees, approval policies, files,
  idempotency keys and outbox messages.
- Business tables use UUID identifiers, `timestamptz`, `numeric` plus currency
  for money, and optimistic `version` columns where concurrent changes matter.
- JSONB is limited to controlled configuration and event payloads, not core
  business entities.
- Soft delete is the standard for business entities and configuration, with
  `deleted_at`, `deleted_by_user_id` and `deleted_reason`. EF query filters,
  partial unique indexes, restore capabilities, audit events and retention
  tests are mandatory. Audit events are append-only; outbox and idempotency
  use technical retention rather than user soft deletion.
- Medical document content is separate from absence status and never available
  to managers.

## Provider portability

The browser never uses Supabase data, storage administration or service-role
credentials. Provider-specific code is restricted to small adapter boundaries:

- `IdentityProvider` for Supabase Auth now and OIDC/SAML providers later.
- `FileStorage` for Supabase Storage now and S3/MinIO/Azure Blob later.
- `WhatsAppGateway`, `EmailGateway` and `SmsGateway` per delivery channel.
- `AiTaskProfile` plus protocol adapters for OpenAI-compatible APIs and the
  Anthropic Messages API.

Do not hide PostgreSQL behind a generic persistence abstraction. EF Core and
Npgsql are the normal persistence path; precise SQL is allowed for RLS,
critical database commands and performance-proven reports.

## Files and sensitive data

The API authorizes an upload intent, the client uploads directly through a
short-lived private signed URL, and the API records completion, checksum,
classification and audit data. Files remain in quarantine until type/size
validation and malware scanning pass. Downloads require fresh authorization
and a short-lived URL. No bucket is public.

## Outbox and notifications

Business state and an outbox event commit in one PostgreSQL transaction. A
worker retries delivery with exponential backoff and consumers use the event
identifier for idempotency. The initial notification source of truth is
in-app notification plus e-mail. WhatsApp uses the official Meta Business
Platform behind an adapter and must record opt-in, template, delivery state,
retry and audit data. SMS is exceptional fallback.

## MCP and AI

MCP is first-class in every module, but tools are job-oriented rather than
CRUD copies. Every tool declares purpose, input/output schema, explicit
company context, capability, risk, idempotency and audit policy. Tool calls
always enforce authorization even when discovery hides unavailable tools.

Financial, external and irreversible work follows prepare/confirm execution.
The confirmation token is single-use, short lived and bound to actor, company,
action and payload hash. In the first release AI may read, summarize and
draft. A human confirms financial/external actions.

Aligned with ADR 0005, the AI module owns the task-profile contract and
configuration. A host/application layer selects an approved profile after
Foundation supplies trusted identity, company and capability context; business
code never selects provider/model names. Store metadata, hashes, policy/prompt versions,
cost and outcomes, not raw prompts/responses by default. Never send secrets.
Financial and medical data may be sent only through an explicitly approved
profile; retention mode is visible in that profile. Do not use persistent
conversation/vector/file features at the provider in the pilot.

Document extraction is an internal worker pipeline: private quarantined file,
scan, local text extraction where possible, vision-capable model for scanned
PDF/image, strict JSON-schema output, deterministic validation and human
review. It never validates or rejects a medical certificate by itself.

## Audit and observability

Audit is durable application data, not operational logging. It records actor,
company, source (`web`, `mcp` or `worker`), action, resource, outcome,
correlation identifier, reason and redacted metadata. Successes write in the
business transaction; denied attempts are also auditable. Application roles
cannot update or delete audit events.

`Foundation.Observability` standardizes request context, PII redaction,
structured logs, traces, metrics and readiness/liveness checks. The
correlation identifier crosses web, API, MCP, worker, outbox and external
provider calls. Do not log prompts, medical details, tokens, passwords or
file contents. Instrument operationally useful metrics only: latency/errors,
outbox retries, webhook processing, uploads, authorization denials and AI
cost/usage.

## Tests and quality gates

- Unit tests cover domain rules, state transitions, alçadas and permissions.
- Integration tests use real PostgreSQL containers, migrations, API and MCP
  hosts. Supabase local validates Auth/Storage adapters.
- Browser journeys use Playwright; frontend component/query tests use Vitest,
  React Testing Library and MSW.
- BDD specifications use English Gherkin with Reqnroll plus xUnit for
  high-value user, authorization and AI scenarios.
- AI security uses deterministic assertions for tools, permissions, company
  context, idempotency, audit and data minimization. Dataset evaluations score
  response quality separately and cannot replace deterministic guards.
- Mock only external/non-deterministic boundaries such as Meta, model APIs,
  e-mail/SMS and malware engines. Do not mock own authorization, repositories,
  use cases or MCP handlers.
- `tests/public` is available to builders. `quality/private` holds abuse,
  regression and evaluation suites. This is local process isolation, not
  filesystem security: builders are instructed not to access it and a separate
  verifier executes it.

Every feature definition of done includes appropriate authorization, audit,
observability, public tests and a documented MCP-tool decision.

## Documentation and memory

Git plus Markdown is the canonical memory. Required documentation follows the
changed artifact: BDD/module docs for behavior, OpenAPI for API contracts, MCP
manifest/schema for tools, ADR for architectural/security/provider choices,
migration notes for data changes and runbooks for operational changes.

Basic Memory is not installed in this environment. If installed later, it is
only a disposable local index derived from Markdown and is never canonical.
Graphify is deferred until the repository has enough code/dependency surface
to justify code-graph maintenance. Neither tool may contain secrets or replace
the repository documentation.

Create a reusable project skill only after a workflow is repeated or is
high-risk and stable. Likely candidates after real usage are module creation,
MCP tool review, approval-flow review, sensitive-document handling and
documentation updates.

## Environment and delivery

- Local: Docker Compose, Supabase CLI local stack, PostgreSQL test containers,
  committed migrations/seeds and developer-safe configuration.
- Homologation: isolated Supabase project and containers; no production data.
- Production: isolated project and containerized hosts, with a final provider
  decision captured in GitHub issue #70.
- GitHub Actions runs lightweight checks under the free allowance with a zero
  spending cap. Private main-branch protection for a private GitHub repository
  requires a paid plan; until then this is an explicit process limitation.
- Backup, restore, retention, RPO and RTO remain a required decision in GitHub
  issue #71 before production.

## Deferred decisions and issue links

- Paguai discovery and state machine: #45; Paguai epic: #4.
- Deploy/cost decision: #70.
- Backup/recovery/retention: #71.
- WhatsApp medical-submission bot: to be tracked from its dedicated issue.
- Public/third-party API compatibility and full contract deprecation: defer
  until external consumers exist. Internal contracts remain clear and
  additive where practical, but do not run multiple API versions now.
