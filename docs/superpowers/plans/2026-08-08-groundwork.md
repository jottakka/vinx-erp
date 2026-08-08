# Vinx ERP Groundwork Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` or `superpowers:executing-plans`. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a reproducible, secure and testable local foundation for the Vinx ERP modular monolith without business workflows.

**Architecture:** One .NET 10 modular monolith has API, Worker and MCP hosts plus a React/Vite PWA. Supabase is used only through server-side adapters and local CLI. Git-tracked Markdown is canonical; Basic Memory only indexes `docs/` locally.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core/Npgsql, React 19, TypeScript, Vite, React Router, TanStack Query/Table, React Hook Form, Zod, MUI, Vitest, Playwright, Reqnroll/xUnit, Testcontainers, Supabase CLI, Docker Compose, OpenTelemetry and GitHub Actions.

## Global Constraints

- Use an isolated `codex/groundwork` worktree; never develop on `main`.
- Do not add microservices, broker, Redis, Kafka, Kubernetes, generic repositories, base services or generic command bus.
- Browser code cannot access Supabase data, Storage administration or service-role credentials.
- Future business data requires explicit `company_id` and every command requires explicit company context.
- Soft delete is standard for business/configuration data; audit events are append-only.
- Integration/authorization tests use real PostgreSQL/Testcontainers; mock only external nondeterministic providers.
- `quality/private` is process isolation only. Builders must not read/run it; a verifier owns it.
- Update behavior docs, ADRs, migration notes and runbooks with the change that makes them stale.
- Pin dependencies in `package-lock.json`, `packages.lock.json` and `global.json`.
- Create a project skill only after a workflow repeats twice, or immediately when it is high-risk and stable.

---

## File Map

| Path | Responsibility |
|---|---|
| `.gitignore`, `.gitattributes`, `.editorconfig`, `global.json`, `.nvmrc` | Hygiene and reproducible tools. |
| `AGENTS.md` | Agent boundaries, private-test isolation and documentation rules. |
| `package.json`, `package-lock.json` | Root npm workspace and pinned Supabase CLI. |
| `Directory.Build.props`, `Directory.Packages.props`, `packages.lock.json` | .NET compiler, analyzer and lock policy. |
| `apps/web/` | React PWA and public frontend tests. |
| `src/Hosts/`, `src/Foundation/` | Three thin hosts and Foundation. |
| `tests/public/`, `quality/private/` | Builder-visible and verifier-owned test suites. |
| `supabase/`, `infra/compose/` | Local Supabase and optional developer services. |
| `.github/workflows/` | Free-tier-safe automation. |
| `docs/adr`, `docs/modules`, `docs/runbooks`, `docs/handoffs` | Canonical decisions and operations. |

### Task 1: Isolate the groundwork branch and establish repository policy

**Files:**
- Create: `.gitignore`, `.gitattributes`, `.editorconfig`, `global.json`, `.nvmrc`, `AGENTS.md`.
- Modify: `README.md`.
- Test: Git hygiene and tool-version commands.

**Interfaces:**
- Consumes: approved architecture design.
- Produces: clean `codex/groundwork` worktree and rules all tasks follow.

- [ ] **Step 1: Create and enter isolated worktree**

```bash
git worktree add ../vinx-erp-groundwork -b codex/groundwork
cd ../vinx-erp-groundwork
```

Expected: branch is `codex/groundwork` and the worktree is clean.

- [ ] **Step 2: Pin base tools**

Create `global.json`:

```json
{
  "sdk": {
    "version": "10.0.302",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

Create `.nvmrc` containing `22.23.2`. Set `.editorconfig` to UTF-8/LF/final newline, two spaces for JSON/YAML/TypeScript and four for C#.

Create `.gitignore` with this minimum content:

```gitignore
node_modules/
dist/
coverage/
.env
.env.*
!.env.example
bin/
obj/
TestResults/
artifacts/
playwright-report/
test-results/
supabase/.temp/
```

Create `.gitattributes` with `* text=auto eol=lf`, and mark `*.png`, `*.jpg`, `*.jpeg`, `*.pdf` and `*.ico` as `binary`.

- [ ] **Step 3: Establish agent rules**

Create `AGENTS.md`:

```markdown
# Vinx ERP agent rules

- Read the architecture design and module docs before behavior changes.
- Do not read, list or execute quality/private; a separate verifier owns it.
- Do not write directly to main; use a codex/* branch and pull request.
- Run the smallest public test, then the complete public verification command.
- Do not add an abstraction, adapter, skill or dependency without a present use case and documented reason.
- Authorization, explicit company context, audit, observability and documentation are definition-of-done requirements.
- Never put secrets, tokens, medical content, prompt bodies or file content in logs, fixtures or Markdown.
```

- [ ] **Step 4: Replace README with a route map and commit**

Link architecture design, local-development runbook, ADR index and public verification. State legacy is reference-only. Run:

```bash
git diff --check
dotnet --version
node --version
git add .gitignore .gitattributes .editorconfig global.json .nvmrc AGENTS.md README.md
git commit -m "chore: establish repository policy"
```

Expected: no whitespace errors; .NET is permitted 10.0.3xx and Node is v22.23.2.

### Task 2: Build developer toolchain and local Supabase entrypoint

**Files:**
- Create: `package.json`, `package-lock.json`, `supabase/config.toml`, `infra/compose/compose.dev.yml`.
- Create: `docs/runbooks/local-development.md`, `docs/runbooks/basic-memory.md`.
- Test: toolchain, Supabase and Compose commands.

**Interfaces:**
- Consumes: Docker Desktop 29.6.2+, Basic Memory 0.22.1 and Task 1.
- Produces: reproducible local commands and no business data schema.

- [ ] **Step 1: Write the failing toolchain command**

Create root `package.json`:

```json
{
  "private": true,
  "workspaces": ["apps/*"],
  "scripts": {
    "tools:check": "node --version && dotnet --version && docker version --format '{{.Server.Version}}' && supabase --version"
  }
}
```

Run `npm run tools:check`. Expected: failure because project-local Supabase CLI is absent.

- [ ] **Step 2: Add local Supabase, start it and verify it**

```bash
npm install --save-dev supabase
npm pkg set scripts.supabase="supabase"
npm run tools:check
npx supabase init
npx supabase start
npx supabase status
```

Expected: resolved lockfile and service URLs. Do not install a global Supabase CLI or create tables/RLS yet.

- [ ] **Step 3: Document disposable semantic index and secrets**

State in `basic-memory.md`: `vinx-erp-docs` indexes `docs/`; `~/.basic-memory` SQLite is disposable; durable decisions require Git commits; usage commands are `basic-memory status`, `basic-memory reindex` and `basic-memory doctor`.

State in `local-development.md`: `npm install`, `npx supabase start`, service URLs, stop procedure and ignored `.env.local`.

Create `infra/compose/compose.dev.yml`:

```yaml
name: vinx-erp
services: {}
networks:
  vinx-erp:
    name: vinx-erp
```

- [ ] **Step 4: Validate and commit**

```bash
npm run tools:check
npx supabase status
docker compose -f infra/compose/compose.dev.yml config
git diff --check
git add package.json package-lock.json supabase infra/compose docs/runbooks
git commit -m "chore: add local development foundation"
```

Expected: generated secrets are not staged and Compose has no duplicate PostgreSQL/Auth/Storage service.

### Task 3: Scaffold React PWA and deterministic public test seams

**Files:**
- Create: `apps/web/` via Vite.
- Create: `apps/web/src/app/{providers,router,query-client}.tsx`.
- Create: `apps/web/src/test/{setup,msw/server}.ts`.
- Create: `apps/web/{vitest,playwright}.config.ts`.
- Test: provider unit test and one browser health journey.

**Interfaces:**
- Consumes: root npm workspace.
- Produces: `AppProviders` and `createAppRouter` with no Supabase browser client.

- [ ] **Step 1: Generate web project and dependencies**

```bash
npm create vite@latest apps/web -- --template react-ts
npm --workspace apps/web install react-router-dom @tanstack/react-query @tanstack/react-table @tanstack/react-virtual react-hook-form zod @hookform/resolvers @mui/material @mui/icons-material @emotion/react @emotion/styled
npm --workspace apps/web install --save-dev vitest @testing-library/react @testing-library/jest-dom @testing-library/user-event jsdom msw @playwright/test biome
npx playwright install chromium
```

Expected: root lockfile pins all resolved packages.

- [ ] **Step 2: Write failing provider test**

```tsx
import { render, screen } from "@testing-library/react";
import { AppProviders } from "./providers";

it("provides the application shell", () => {
  render(<AppProviders><p>Vinx shell</p></AppProviders>);
  expect(screen.getByText("Vinx shell")).toBeInTheDocument();
});
```

Run `npm --workspace apps/web run test -- --run src/app/providers.test.tsx`. Expected: fail because `AppProviders` is absent.

- [ ] **Step 3: Implement only the shell**

```tsx
export function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: 1, staleTime: 30_000 } } });
}

export function AppProviders({ children }: { children: React.ReactNode }) {
  return <QueryClientProvider client={createQueryClient()}>{children}</QueryClientProvider>;
}
```

Expose `createAppRouter()` with one health route. Do not add login, global store, permissions, menus or Supabase.

- [ ] **Step 4: Configure checks, verify and commit**

Configure Vitest/MSW, Playwright preview server and scripts `test`, `test:e2e`, `lint`, `format:check` and `build`. Run:

```bash
npm --workspace apps/web run lint
npm --workspace apps/web run format:check
npm --workspace apps/web run test -- --run
npm --workspace apps/web run build
npm --workspace apps/web run test:e2e
git add apps/web package.json package-lock.json
git commit -m "feat: scaffold web application shell"
```

Expected: public frontend checks pass without a backend.

### Task 4: Scaffold .NET hosts and real PostgreSQL testing

**Files:**
- Create: `Vinx.Erp.sln`, `Directory.Build.props`, `Directory.Packages.props`, `packages.lock.json`, `.config/dotnet-tools.json`.
- Create: `src/Hosts/{Vinx.Erp.ApiHost,Vinx.Erp.WorkerHost,Vinx.Erp.McpHost}/`.
- Create: `src/Foundation/Vinx.Erp.Foundation/`.
- Create: public unit and integration test projects.
- Test: API health and Testcontainers PostgreSQL fixture.

**Interfaces:**
- Consumes: Task 1 SDK and Docker.
- Produces: `MapFoundationHealthEndpoints`, health-enabled hosts and `PostgresContainerFixture`.

- [ ] **Step 1: Create solution, tools and projects**

```bash
dotnet new sln --name Vinx.Erp
dotnet new tool-manifest
dotnet tool install dotnet-ef
dotnet tool install csharpier
dotnet new web --name Vinx.Erp.ApiHost --output src/Hosts/Vinx.Erp.ApiHost
dotnet new worker --name Vinx.Erp.WorkerHost --output src/Hosts/Vinx.Erp.WorkerHost
dotnet new web --name Vinx.Erp.McpHost --output src/Hosts/Vinx.Erp.McpHost
dotnet new classlib --name Vinx.Erp.Foundation --output src/Foundation/Vinx.Erp.Foundation
dotnet sln Vinx.Erp.sln add src/Hosts/Vinx.Erp.ApiHost src/Hosts/Vinx.Erp.WorkerHost src/Hosts/Vinx.Erp.McpHost src/Foundation/Vinx.Erp.Foundation
```

Expected: do not create empty Paguai/HR/Kanban/ServiceDesk projects.

- [ ] **Step 2: Write failing API health test**

```csharp
[Fact]
public async Task GetHealth_ReturnsHealthy()
{
    await using var app = new VinxApiFactory();
    var response = await app.CreateClient().GetAsync("/health");

    response.EnsureSuccessStatusCode();
    Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
}
```

Run `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests`. Expected: fail before API/test factory exist.

- [ ] **Step 3: Implement smallest host boundary**

```csharp
public static class FoundationHealthEndpoints
{
    public static IEndpointRouteBuilder MapFoundationHealthEndpoints(this IEndpointRouteBuilder endpoints)
        => endpoints.MapGet("/health", () => Results.Text("Healthy"));
}
```

Map it from API host and use `WebApplicationFactory<Program>`. Worker/MCP host have only health and OpenTelemetry bootstrap. No command bus, repository, authorization handler or MCP tool.

- [ ] **Step 4: Add real fixture, locked restore, verify and commit**

Add Testcontainers.PostgreSql, Npgsql, xUnit, MVC Testing and HealthChecks. Fixture opens `NpgsqlConnection` and asserts `SELECT 1`. Set warnings-as-errors, nullable, deterministic builds and locked restore; centralize versions. Run:

```bash
dotnet tool restore
dotnet restore --locked-mode
dotnet format whitespace --verify-no-changes
dotnet build --no-restore --locked-mode
dotnet test --no-build --locked-mode
git add Vinx.Erp.sln Directory.Build.props Directory.Packages.props packages.lock.json .config src tests/public
git commit -m "feat: scaffold modular .NET foundation"
```

Expected: health and PostgreSQL container tests pass.

### Task 5: Establish authorization, audit, observability and MCP contracts

**Files:**
- Create: four ADRs (modular monolith, provider boundaries, MCP contract, audit/observability).
- Create: Foundation and MCP-tool checklist module docs.
- Create: `docs/contracts/openapi/README.md`, `docs/contracts/mcp/README.md`.
- Create: public `Authorization.feature` and `quality/private/README.md`.
- Test: documentation placeholder scan.

**Interfaces:**
- Consumes: approved architecture design.
- Produces: written contract every Foundation/module change implements.

- [ ] **Step 1: Record ADRs and Foundation boundary**

Use:

```markdown
# ADR NNNN: Title

## Status
Accepted

## Context

## Decision

## Consequences
```

Foundation owns memberships, named capabilities/scopes, configurable approval policy, IdentityProvider/FileStorage boundaries, audit and observability; business rules remain in modules.

- [ ] **Step 2: Define usable MCP tools**

Checklist requires job intent, `empresa_id`, capability, risk, JSON input/output, audit event, correlation ID and write idempotency. Prohibit generic CRUD/SQL. Require prepare/confirm bound to actor/company/action/payload hash for external, financial or irreversible actions.

- [ ] **Step 3: Create public BDD language and private custody policy**

```gherkin
Feature: Company context is explicit
  Scenario: A command without a company context is denied
    Given Ana is authenticated
    When she prepares a payment approval without an empresa_id
    Then the command is denied
    And an authorization denial is auditable
```

Tag the scenario with `@ignore` when Task 6 creates its Reqnroll project. The first Foundation slice removes that tag and adds bindings with the application code. Private README says verifier owns private tests and reports only redacted actionable evidence.

- [ ] **Step 4: Verify and commit contracts**

```bash
rg -n "TODO|TBD|implement later" docs/adr docs/modules docs/contracts quality/private && exit 1 || true
git diff --check
git add docs/adr docs/modules docs/contracts quality/private tests/public/Vinx.Erp.Foundation.Bdd
git commit -m "docs: establish foundation contracts"
```

### Task 6: Wire layered tests, CI and first-slice handoff

**Files:**
- Create: public Reqnroll and architecture test projects.
- Create: `quality/private/Vinx.Erp.PrivateQualityTests/`.
- Create: `scripts/{verify-public,verify-private}.sh`.
- Create: `.github/workflows/{ci,private-quality}.yml`.
- Create: `docs/handoffs/foundation-first-slice.md`.
- Test: public runner, Supabase status and Basic Memory health.

**Interfaces:**
- Consumes: public frontend/backend commands and BDD language.
- Produces: builder-visible public runner and verifier-only private runner, plus role-separated next work.

- [ ] **Step 1: Add BDD discovery and architecture guard**

Create the Reqnroll/xUnit project, copy the Task 5 feature into it and add `@ignore` directly above the Feature line. Run the scenario once and confirm it is skipped, not silently absent. Add this npm script:

```json
{
  "scripts": {
    "test:no-direct-supabase": "! rg -n 'supabase|createClient' apps/web/src"
  }
}
```

Run `npm run test:no-direct-supabase`. Expected: pass. The first Foundation slice removes `@ignore`, adds its BDD bindings and replaces this text guard with a stronger contract test when shared frontend contracts exist.

- [ ] **Step 2: Create public and private runner scripts**

```bash
#!/usr/bin/env bash
set -euo pipefail
npm ci
npm --workspace apps/web run lint
npm --workspace apps/web run format:check
npm --workspace apps/web run test -- --run
npm --workspace apps/web run build
npm run test:no-direct-supabase
dotnet tool restore
dotnet restore --use-lock-file
dotnet restore --locked-mode
dotnet format whitespace --verify-no-changes
dotnet build --no-restore --locked-mode
dotnet test --no-build --locked-mode --filter 'FullyQualifiedName!~PrivateQuality'
```

Private script first verifies it is a verifier-owned clone, runs only private suite and emits test name/outcome/redacted evidence/correlation ID, never test source.

- [ ] **Step 3: Configure free-tier CI**

`ci.yml` runs public runner on pull requests and non-main branch pushes. `private-quality.yml` is manual verifier-only. Document GitHub Free private-repository protection limitation: no agent pushes/merges main operationally.

- [ ] **Step 4: Verify and create handoff**

```bash
chmod +x scripts/verify-public.sh scripts/verify-private.sh
./scripts/verify-public.sh
basic-memory reindex
basic-memory status
basic-memory doctor
npx supabase status
git diff --check
```

Create handoff roles:

```markdown
## Builder
Implement Foundation membership read access and API capability map. Do not read quality/private.

## Quality author
Write private abuse tests for forged/missing company context and cross-company cache leakage. Do not change production code.

## Verifier
Run public/private gates in separate worktrees. Report only redacted failures needed for a builder fix.
```

Bind it to the BDD, audit allow/deny events and OpenTelemetry correlation ID; exclude login, payment approval, file upload and MCP write actions.

- [ ] **Step 5: Commit final groundwork**

```bash
git add tests/public quality/private scripts .github docs README.md
git commit -m "ci: add quality gates and foundation handoff"
git log --oneline main..HEAD
git status --short
```

Expected: clean `codex/groundwork` branch, ready for review/PR. Never merge from an agent session.
