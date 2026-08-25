# Issue #114 Real E2E Fixtures Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Subir um banco PostgreSQL descartável com dados multiempresa ricos,
ApiHost real em Kestrel e Vite por um comando test-only, pronto para inspeção
manual Luna sem password, token ou bypass de produção.

**Architecture:** A infraestrutura existente continua dona de container,
migrations, roles, autenticação de teste e cleanup. Um seeder transacional cria
o dataset; `E2eFixtureHost` liga a factory a `UseKestrel(0)` e valida contratos
reais antes de anunciar prontidão. Um launcher Node inicia host e Vite, sem
copiar fixture ou iniciar Supabase/Worker/providers.

**Tech Stack:** .NET 10, ASP.NET Core Minimal API/controllers,
`Microsoft.AspNetCore.Mvc.Testing` 10.0.11, EF Core/Npgsql, PostgreSQL
Testcontainers, Node.js 22 built-ins, Vite, Vitest.

## Global Constraints

- Nunca acessar `quality/private`.
- Nenhuma migration/seed de produção, password, JWT, refresh token, service
  role, connection string, dado real, arquivo, prompt ou conteúdo médico.
- Profile vem somente de argumento allow-listed do processo test-only; nunca de
  HTTP/header/query/cookie/browser.
- Browser/API continuam usando autorização, RLS, auditoria e session gate reais.
- Exatamente um PostgreSQL, um ApiHost, um Vite e um browser no fluxo manual.
- Não iniciar Supabase, WorkerHost, AI, Meta, ClamAV ou Wolverine storage.
- Sem dependência nova e sem copiar `FoundationPostgresFixture`.
- Startup não publica URL antes de migration, seed e readback completo.
- Logs/erros só podem conter fase/categoria fixa e URL loopback.
- #115 permanece fora: não adicionar suite Playwright real automatizada.

---

### Task 1: Catálogo de profiles e seed determinístico

**Files:**
- Create: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/E2eFixtureData.cs`
- Create: `tests/public/Vinx.Erp.Foundation.IntegrationTests/E2eFixtureSeederTests.cs`

**Interfaces:**
- Produces: `E2eFixtureProfileCatalog.TryGet(string, out E2eFixtureProfile?)`.
- Produces: `E2eFixtureSeeder.SeedAsync(FoundationPostgresFixture, CancellationToken) -> E2eDatasetReceipt`.
- Produces: `E2eFixtureSeeder.VerifyAsync(FoundationPostgresFixture, E2eDatasetReceipt, CancellationToken)`.

**Canonical IDs:**

| Row | UUID |
|---|---|
| Group | `a1000000-0000-4000-8000-000000000001` |
| Alfa/Beta/Gama | `a2000000-0000-4000-8000-000000000001/2/3` |
| Maria/Beatriz/João/Clara | `b1000000-0000-4000-8000-000000000001/2/3/4` |
| External identities | `b2000000-0000-4000-8000-000000000001/2/3/4` |
| Maria Alfa/Beta | `b3000000-0000-4000-8000-000000000001/2` |
| Beatriz Alfa | `b3000000-0000-4000-8000-000000000003` |
| João Beta | `b3000000-0000-4000-8000-000000000004` |
| Clara Gama | `b3000000-0000-4000-8000-000000000005` |

Use o mesmo padrão sequencial para units `c100...`, teams `c200...`, roles
`d100...`, assignments `d200...`, grants `d300...`, employees `e100...`,
Paguai `f100...`, cards `f200...`, notifications `f300...` e request IDs
`f400...`. Todos mantêm version nibble `4` e variant `8`.

- [ ] **Step 1: Escrever RED do catálogo e banco fresco**

Adicionar fatos que exigem profiles `maria|beatriz|joao|clara`, rejeitam nome
desconhecido e fazem a segunda chamada `SeedAsync` no mesmo banco falhar com
`InvalidOperationException("e2e fixture database is not empty")`.

- [ ] **Step 2: Rodar RED**

Run:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~E2eFixtureSeederTests'
```

Expected: FAIL porque catálogo/seeder não existem.

- [ ] **Step 3: Implementar catálogo e seed mínimo**

Profiles fixos:

```csharp
public sealed record E2eFixtureProfile(
    string Name,
    Guid UserId,
    Guid ExternalIdentityId,
    string Subject,
    string DisplayName,
    string Email);

public sealed record E2eDatasetReceipt(
    Guid GroupId,
    Guid AlfaCompanyId,
    Guid BetaCompanyId,
    Guid GamaCompanyId);
```

`SeedAsync` abre `OpenAdministratorConnectionAsync`, inicia uma transação,
confirma `NOT EXISTS (SELECT 1 FROM foundation.users)`, insere com parâmetros:

- 1 group, 3 companies, 4 users/external identities, 5 memberships;
- 3 units, 3 teams, 5 roles, assignments e grants por `capabilities.name`;
- employees Maria/Beatriz em Alfa, Maria/João em Beta, Clara em Gama;
- 4 Paguai drafts, 5 Atendimento cards;
- 3 intents delivered para Maria (uma lida), 1 delivered para Clara.

Use timestamp `2026-08-01T12:00:00Z`, BRL, datas fixas, `state='rascunho'`,
cards `novo|em_andamento|concluido`; intents delivered usam
`provider_attempt_id`, `provider_claimed_at`, `provider_resolved_at`,
`failure_reason=NULL` e par `read_at/read_by_user_id` válido.

- [ ] **Step 4: GREEN e verificação de conteúdo**

No mesmo teste, consultar contagens/IDs, provar Maria sem Gama, grants distintos
e linhas por schema. Run o filtro até PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/E2eFixtureData.cs \
  tests/public/Vinx.Erp.Foundation.IntegrationTests/E2eFixtureSeederTests.cs
git commit -m "test(e2e): add deterministic multi-company seed"
```

---

### Task 2: Factory test-only e host Kestrel com readback

**Files:**
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationApiFactory.cs`
- Create: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/E2eFixtureHost.cs`
- Create: `tests/public/Vinx.Erp.Foundation.IntegrationTests/RealE2eFixtureHostTests.cs`

**Interfaces:**
- Consumes: `E2eFixtureProfile`, `E2eFixtureSeeder`, `E2eDatasetReceipt`.
- Produces: `FoundationApiFactory.CreateWithE2eProfileAsync(string, CancellationToken) -> E2eFixtureFactoryContext`.
- Produces: `E2eFixtureHost.StartAsync(string, CancellationToken)`.
- Produces: `E2eFixtureHost.Origin`, `ProfileName`, `Receipt`, `DisposeAsync()`.

```csharp
public sealed record E2eFixtureFactoryContext(
    FoundationApiFactory Factory,
    E2eFixtureProfile Profile,
    E2eDatasetReceipt Receipt);

public enum E2eFixtureHostFailurePhase
{
    FixtureStart,
    Seed,
    HostStart,
    Readback,
}

public sealed class E2eFixtureHostException(
    E2eFixtureHostFailurePhase phase,
    Exception innerException)
    : Exception("E2E fixture host failed.", innerException)
{
    public E2eFixtureHostFailurePhase Phase { get; } = phase;
}
```

- [ ] **Step 1: RED do host real**

Um único fato chama `StartAsync("maria")` e exige:

- origin com scheme/host exatos `http://127.0.0.1` e porta dinâmica positiva;
- `/health` 200;
- bootstrap contém Alfa/Beta e não Gama;
- employees Alfa, Paguai Beta, cards Alfa e notifications inbox retornam linhas;
- leitura direta de employee/Paguai Gama retorna 403/404 conforme contrato;
- profile desconhecido falha antes de iniciar container.

- [ ] **Step 2: Rodar RED**

Run o filtro `FullyQualifiedName~RealE2eFixtureHostTests`; expected missing API.

- [ ] **Step 3: Estender factory sem bypass novo**

Adicionar factory method que:

1. resolve profile allow-listed;
2. constrói `TestPrincipalClaims` com issuer de teste, subject do profile,
   session UUID de processo e AMR password/email;
3. chama `PrepareAsync` com `provisionWolverineStorage:false`;
4. chama seed/verify antes de devolver
   `E2eFixtureFactoryContext(factory, profile, receipt)`.

Se `PrepareAsync` falhar, descartar a factory e lançar phase `FixtureStart`; se
seed/verify falhar, descartar a factory e lançar phase `Seed`. Profile inválido
é erro de argumento antes de criar fixture.

Não adicionar header seletor nem configuração no ApiHost de produção.

- [ ] **Step 4: Implementar E2eFixtureHost**

```csharp
public sealed class E2eFixtureHost : IAsyncDisposable
{
    public Uri Origin { get; }
    public string ProfileName { get; }
    public E2eDatasetReceipt Receipt { get; }
    public static Task<E2eFixtureHost> StartAsync(
        string profileName,
        CancellationToken cancellationToken);
}
```

Chamar `factory.UseKestrel(0)` antes de `CreateClient`, obter
`client.BaseAddress`, recusar host diferente de `127.0.0.1`, executar os
readbacks com timeout e só então retornar. `StartAsync` mantém referências
locais nullable para context/client; qualquer falha entra em `catch`, descarta
client, factory/Kestrel e fixture nessa ordem e relança
`E2eFixtureHostException` com a fase definida no boundary, nunca por matching
de texto.

- [ ] **Step 5: Concorrência real com dois hosts**

Adicionar um fato estratégico que inicia dois `E2eFixtureHost.StartAsync`
simultaneamente, confirma connections/bancos e `Origin` distintos, obtém
antiforgery no host A e cria um employee Alfa pela API real. Listar employees em
A e B: somente A contém a nova pessoa. Encerrar ambos e confirmar portas/API e
dois Testcontainers removidos.

- [ ] **Step 6: GREEN e commit**

Run filtro até PASS; aguardar Testcontainers sumir; commit:

```bash
git add tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationApiFactory.cs \
  tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/E2eFixtureHost.cs \
  tests/public/Vinx.Erp.Foundation.IntegrationTests/RealE2eFixtureHostTests.cs
git commit -m "test(e2e): host real API fixture on Kestrel"
```

---

### Task 3: Executável test-only persistente

**Files:**
- Create: `tests/tools/Vinx.Erp.Foundation.E2eHost/Vinx.Erp.Foundation.E2eHost.csproj`
- Create: `tests/tools/Vinx.Erp.Foundation.E2eHost/Program.cs`
- Create: `tests/tools/Vinx.Erp.Foundation.E2eHost/packages.lock.json` (generated)
- Modify: `Vinx.Erp.sln`
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/E2eFixtureHost.cs`
- Test: `tests/public/Vinx.Erp.Foundation.IntegrationTests/E2eFixtureHostOptionsTests.cs`

**Interfaces:**
- Produces CLI: `dotnet run --project tests/tools/Vinx.Erp.Foundation.E2eHost -- --profile maria`.
- Produces readiness line com prefixo literal
  `E2E_API_ORIGIN=http://127.0.0.1:` seguido da porta decimal escolhida por
  Kestrel.

- [ ] **Step 1: RED do parser**

Criar `E2eFixtureHostOptions.TryParse(string[], out string profile)` dentro de
`Infrastructure/E2eFixtureHost.cs`. Cobrir exatamente `--profile maria`,
`--profile beatriz`, `--profile joao` e `--profile clara`; rejeitar ausente,
duplicado, flag extra e profile desconhecido.

- [ ] **Step 2: Implementar Program mínimo**

Program retorna 2 para argumentos inválidos. Com argumentos válidos, usa
`CancellationTokenSource`, trata SIGINT/SIGTERM, chama `E2eFixtureHost.StartAsync`,
escreve somente a readiness line, aguarda cancelamento e faz dispose. Exceção
faz `switch` somente em `E2eFixtureHostException.Phase` e escreve uma categoria
em stderr: `FixtureStart -> E2E_HOST_FAILED=fixture_start_failed`, `Seed ->
seed_failed`, `HostStart -> host_start_failed`, `Readback -> readback_failed`.
Nunca serializar exception/inner message; retornar 1.

- [ ] **Step 3: Gerar lock e testar processo**

Adicionar projeto à solution,
`dotnet restore tests/tools/Vinx.Erp.Foundation.E2eHost/Vinx.Erp.Foundation.E2eHost.csproj --use-lock-file`, build
locked e um smoke process que espera readiness, envia SIGINT e confirma exit 0
e ausência de Testcontainers.

- [ ] **Step 4: Commit**

```bash
git add Vinx.Erp.sln tests/tools/Vinx.Erp.Foundation.E2eHost \
  tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/E2eFixtureHost.cs \
  tests/public/Vinx.Erp.Foundation.IntegrationTests/E2eFixtureHostOptionsTests.cs
git commit -m "test(e2e): add persistent real fixture host"
```

---

### Task 4: Launcher Node com cleanup próprio

**Files:**
- Create: `scripts/run-real-e2e.mjs`
- Create: `scripts/run-real-e2e.test.mjs`
- Modify: `package.json`

**Interfaces:**
- Produces: `npm run dev:real-e2e -- --profile maria`.
- Consumes readiness `E2E_API_ORIGIN=...`.
- Browser URL: `http://127.0.0.1:4174`.

- [ ] **Step 1: RED com `node:test`**

Exportar e testar `parseLauncherArgs`, `parseReadyOrigin` e `runLauncher(deps)`.
Fakes provam: host failure não inicia Vite; readiness inválida falha; timeout
termina o grupo do host; shutdown termina/aguarda o grupo Vite antes do grupo
host; profile extra/unknown falha.

Run:

```bash
node --test scripts/run-real-e2e.test.mjs
```

- [ ] **Step 2: Implementar launcher**

Usar somente `node:child_process`, `node:readline`, `node:net`, `node:path` e
fetch global.
Checar porta 4174 antes de spawn; iniciar `dotnet run --no-launch-profile` em
process group próprio (`detached:true` em Darwin/Linux); aguardar readiness por
linha com 90s. Resolver o CLI com
`path.resolve(repositoryRoot, "node_modules/vite/bin/vite.js")` e iniciar Vite
diretamente com `process.execPath`, `cwd` igual a
`path.resolve(repositoryRoot, "apps/web")`, argumentos exatos
`--host 127.0.0.1 --port 4174 --strictPort`, também em group próprio, com env
filho `VINX_API_ORIGIN`; poll HTTP com abort timeout; imprimir
`E2E_BROWSER_URL=http://127.0.0.1:4174` e `E2E_PROFILE=` seguido do profile
allow-listed selecionado.

SIGINT/SIGTERM: `process.kill(-child.pid, signal)` somente nos grupos criados;
terminar/aguardar Vite, depois terminar/aguardar host. Nunca usar `pkill`, porta
ampla ou PID não criado pelo launcher.

- [ ] **Step 3: GREEN e commit**

Run Node tests; smoke `--profile desconhecido` retorna nonzero sem child. Um
teste real condicionado por `VINX_RUN_REAL_E2E_SMOKE=1` inicia o launcher,
aguarda browser/API, envia SIGINT, espera exit 0, prova portas 4174/API
fechadas e faz polling até o Testcontainer próprio desaparecer.

```bash
git add scripts/run-real-e2e.mjs scripts/run-real-e2e.test.mjs package.json
git commit -m "test(e2e): add one-command real fixture launcher"
```

---

### Task 5: Proxy Notifications, documentação e gate final

**Files:**
- Modify: `apps/web/vite.config.ts`
- Modify: `apps/web/src/app/vite-api-proxy.test.ts`
- Modify: `docs/runbooks/local-development.md`
- Create: `docs/runbooks/real-e2e-fixture.md`
- Create: `docs/verification/e2e-real-fixtures-test-cases.md`
- Modify: `docs/superpowers/specs/2026-08-25-issue-114-real-e2e-fixtures-design.md`
- Modify: `docs/superpowers/plans/2026-08-25-issue-114-real-e2e-fixtures.md`

**Interfaces:**
- Adds development proxy `/notifications` com target loopback existente.
- Documents one-command lifecycle and exact verification boundary.

- [ ] **Step 1: RED do proxy**

Estender `vite-api-proxy.test.ts`: `/notifications/inbox` chega ao stub API e
`/notificacoes` continua SPA local. Rodar teste e observar RED.

- [ ] **Step 2: GREEN mínimo**

Adicionar apenas `/notifications` ao map existente; sem rewrite/changeOrigin.

- [ ] **Step 3: Documentar**

Runbook inclui pré-requisitos, comando, profiles/dados, URLs, cleanup,
diagnóstico seguro e limites: não prova login/provider/MFA/produção. Manifesto
mapeia as cinco classes BDD para testes/rota manual; concorrência aponta para o
novo teste de dois `E2eFixtureHost` e usa o teste existente de endpoints como
evidência complementar.

- [ ] **Step 4: Gate final serial**

Executar:

```bash
dotnet restore tests/tools/Vinx.Erp.Foundation.E2eHost/Vinx.Erp.Foundation.E2eHost.csproj --locked-mode
dotnet build tests/tools/Vinx.Erp.Foundation.E2eHost/Vinx.Erp.Foundation.E2eHost.csproj --no-restore
node --test scripts/run-real-e2e.test.mjs
npm --workspace apps/web run test -- --run src/app/vite-api-proxy.test.ts
npm --workspace apps/web run lint
npm --workspace apps/web run format:check
npm --workspace apps/web run build
npm run test:no-direct-supabase
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~E2eFixture'
git diff --check
```

- [ ] **Step 5: Recalcular e commit**

Registrar exatamente 20 arquivos totais (19 manuais/source/docs e 1 lockfile
gerado) ou atualizar a contagem real. Estimativa por tarefa, incluindo as 613
linhas já commitadas de design/plano:

| Task | Linhas manuais estimadas |
|---|---:|
| Design/plano já commitidos | 613 |
| Task 1 — catálogo/seed/teste | 450–600 |
| Task 2 — factory/host/readback/concorrência | 350–500 |
| Task 3 — CLI/processo | 120–200 |
| Task 4 — launcher/testes | 260–380 |
| Task 5 — proxy/runbooks/manifesto | 120–200 |
| **Total** | **1.913–2.493** |

A exceção ao alvo de 800 é explícita: sem seed+host+launcher+proxy não existe o
comando manual pedido. Dividir antes do commit somente se ultrapassar 25
arquivos ou misturar #115.

```bash
git add apps/web/vite.config.ts apps/web/src/app/vite-api-proxy.test.ts \
  docs/runbooks docs/verification docs/superpowers package.json
git commit -m "docs(e2e): document deterministic real fixture"
```

- [ ] **Step 6: Gate transversal pós-commit e execução manual**

Atribuir novamente PID/worktree/context e confirmar ausência de stack VINX
alheia. Com escopo congelado, executar exatamente uma vez:

```bash
./scripts/verify-public.sh
git diff --check origin/main...HEAD
```

Depois executar o smoke real do launcher com
`VINX_RUN_REAL_E2E_SMOKE=1 node --test scripts/run-real-e2e.test.mjs`. Se verde,
iniciar `npm run dev:real-e2e -- --profile maria`, confirmar somente um
PostgreSQL/ApiHost/Vite, entregar a URL a Luna e encerrar por SIGINT através do
launcher. O CI continua sendo o gate remoto; o gate local acima existe uma
única vez porque esta PR muda fixture, runner, build e script compartilhados.
