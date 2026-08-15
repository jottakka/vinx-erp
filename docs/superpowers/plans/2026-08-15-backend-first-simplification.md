# Native Backend and MCP Baseline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> `superpowers:subagent-driven-development` (recommended) or
> `superpowers:executing-plans` to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** concluir a simplificação da PR 207 com configuração ASP.NET Core
validada no startup, primitives HTTP nativas e um MCP Streamable HTTP mínimo,
autenticado e baseado no SDK oficial.

**Architecture:** Foundation mantém opções e autenticação Supabase reutilizáveis;
os hosts compõem apenas os middlewares que usam. A API preserva autorização,
auditoria e corpos `{ "code": ... }`; o MCP expõe somente `vinx_ping`, sem
contexto de domínio, em transporte stateless.

**Tech Stack:** .NET/ASP.NET Core 10.0.10,
`ModelContextProtocol.AspNetCore` 2.2.0, xUnit,
`Microsoft.AspNetCore.Mvc.Testing` 10.0.10 e PostgreSQL apenas nos testes
Foundation já existentes.

## Global Constraints

- Não ler, listar ou executar `quality/private`.
- Preservar os nomes atuais das variáveis `FOUNDATION_RUNTIME_CONNECTION`,
  `SUPABASE_STORAGE_URL`, `SUPABASE_STORAGE_SERVICE_ROLE_KEY`,
  `SUPABASE_AUTH_ISSUER` e `SUPABASE_AUTH_AUDIENCE`.
- Nunca incluir valor de segredo em exceção, log, fixture ou documentação.
- Preservar 401 vazio e todos os erros Foundation `{ "code": ... }`.
- Não aplicar `AddValidation()` aos uploads, retry ao Storage ou timeout global.
- MCP aceita nesta fatia apenas token Supabase pré-obtido; OAuth interoperável,
  contexto confiável, `whoAmI` e tools de domínio permanecem em #133/#132/#125.
- O endpoint MCP é `/mcp`, stateless, sem SSE legado e sem JSON-RPC próprio.
- `AllowedHosts` do MCP começa em loopback; deployment deve fornecer hostname
  exato. Qualquer `Origin` presente é negado até existir origem browser aprovada.
- A exceção de tamanho é explícita: o responsável pediu esta extensão na mesma
  PR 207.

---

## Entrega anterior já concluída

Os commits anteriores desta branch já removeram o runtime de aprovação não
hospedado, preservaram as aprovações duráveis e registraram a ordem backend-first.
As tarefas abaixo cobrem somente a extensão aprovada em
`docs/superpowers/specs/2026-08-15-backend-first-simplification.md`.

### Task 1: Opções nativas e autenticação Supabase reutilizável

**Files:**

- Create: `src/Foundation/Vinx.Erp.Foundation/Identity/SupabaseAuthenticationExtensions.cs`
- Modify: `src/Foundation/Vinx.Erp.Foundation/Files/SupabaseStorageOptions.cs`
- Modify: `src/Foundation/Vinx.Erp.Foundation/Files/SupabaseFileStorage.cs`
- Modify: `src/Foundation/Vinx.Erp.Foundation/Vinx.Erp.Foundation.csproj`
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Program.cs`
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Vinx.Erp.ApiHost.csproj`
- Create: `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApiHostPlatformTests.cs`
- Modify: `tests/public/Vinx.Erp.Foundation.UnitTests/SupabaseFileStorageTests.cs`
- Modify generated locks only through `dotnet restore Vinx.Erp.sln`

**Interfaces:**

- Produces: `SupabaseAuthOptions` with `Issuer`, `Audience` and
  `NormalizedIssuer`.
- Produces: `IServiceCollection.AddSupabaseJwtAuthentication(IConfiguration)`.
- Produces: validated `IOptions<SupabaseStorageOptions>` with `BaseUri` and
  `ServiceRoleKey`; the container remains
  `SupabaseStorageOptions.PrivateContainerId`.
- Produces inside ApiHost: `FoundationDatabaseOptions.ConnectionString`.

- [ ] **Step 1: write failing startup-validation tests**

Add theories in `ApiHostPlatformTests` that start `WebApplicationFactory<Program>`
with one invalid setting at a time and require an `OptionsValidationException`:

```csharp
[Theory]
[InlineData("FOUNDATION_RUNTIME_CONNECTION", "")]
[InlineData("SUPABASE_STORAGE_URL", "not-a-uri")]
[InlineData("SUPABASE_STORAGE_SERVICE_ROLE_KEY", "")]
[InlineData("SUPABASE_AUTH_ISSUER", "not-a-uri")]
[InlineData("SUPABASE_AUTH_AUDIENCE", "")]
public void Invalid_required_option_fails_host_startup(string key, string value)
{
    using var factory = new ApiHostFactory(new Dictionary<string, string?>
    {
        [key] = value
    });

    var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    Assert.IsType<OptionsValidationException>(FindRootCause(failure));
    Assert.DoesNotContain("test-service-role-key", failure.ToString(),
        StringComparison.Ordinal);
}
```

- [ ] **Step 2: run RED**

Run:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter FullyQualifiedName~ApiHostPlatformTests.Invalid_required_option_fails_host_startup -v q
```

Expected: FAIL because the current startup throws manual
`InvalidOperationException` before Options validation.

- [ ] **Step 3: implement the minimum Options registration**

Use framework validation, never include configured values in messages:

```csharp
services.AddOptions<SupabaseAuthOptions>()
    .Configure(options =>
    {
        options.Issuer = configuration[SupabaseAuthOptions.IssuerKey];
        options.Audience = configuration[SupabaseAuthOptions.AudienceKey];
    })
    .Validate(options => options.HasAbsoluteHttpIssuer(),
        "SUPABASE_AUTH_ISSUER must be an absolute HTTP(S) URI.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Audience),
        "SUPABASE_AUTH_AUDIENCE must be configured.")
    .ValidateOnStart();
```

Configure named `JwtBearerOptions` from `IOptions<SupabaseAuthOptions>` and
preserve `MapInboundClaims = false`, issuer, audience, key and lifetime checks.
Register database and Storage options with the same `ValidateOnStart` pattern.
Change `SupabaseFileStorage` to consume `IOptions<SupabaseStorageOptions>` and
remove repeated `Validate()` calls.

- [ ] **Step 4: update direct Storage test construction**

Wrap test options with `Options.Create(...)` and keep all existing request,
credential-header, signed-URL and object-inspection assertions unchanged.

- [ ] **Step 5: run GREEN and regression tests**

Run:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests --filter FullyQualifiedName~Supabase -v q
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter "FullyQualifiedName~ApiHostPlatformTests|FullyQualifiedName~IdentityGateEndpointTests" -v q
```

Expected: PASS; invalid config is `OptionsValidationException`, JWT behavior and
Storage behavior remain unchanged.

- [ ] **Step 6: commit**

```bash
git add Directory.Packages.props src/Foundation src/Hosts/Vinx.Erp.ApiHost tests/public/Vinx.Erp.Foundation.IntegrationTests/ApiHostPlatformTests.cs tests/public/Vinx.Erp.Foundation.UnitTests/SupabaseFileStorageTests.cs
git commit -m "refactor(api): validate backend options on start"
```

### Task 2: HealthChecks, ProblemDetails, OpenAPI e limiter nativos

**Files:**

- Modify: `Directory.Packages.props`
- Delete: `src/Foundation/Vinx.Erp.Foundation/FoundationHealthEndpoints.cs`
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Program.cs`
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Vinx.Erp.ApiHost.csproj`
- Modify: `src/Hosts/Vinx.Erp.WorkerHost/Program.cs`
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/ApiHostPlatformTests.cs`
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/HealthEndpointTests.cs`
- Modify: `tests/public/Vinx.Erp.ArchitectureTests/FoundationDependencyTests.cs`
- Create: `tests/public/Vinx.Erp.ArchitectureTests/NativeHostInfrastructureTests.cs`
- Modify generated project locks through restore

**Interfaces:**

- Produces: `/health` through `MapHealthChecks` with exact body `Healthy`.
- Produces: `/openapi/v1.json` only in `Development`.
- Produces: sanitized `application/problem+json` only for uncaught API errors.
- Produces: named fixed-window policy `authenticated`, 60 requests/minute,
  one bounded partition per host and no queue.

- [ ] **Step 1: write failing native-infrastructure tests**

Cover these observable behaviors:

```csharp
[Fact] public async Task Health_uses_native_no_cache_headers();
[Fact] public async Task Development_exposes_openapi_and_production_does_not();
[Fact] public async Task Unhandled_failure_returns_sanitized_problem_details();
[Fact] public async Task Protected_requests_are_bounded_but_health_is_not();
```

The architecture test reads the three public host `Program.cs` files, requires
`AddHealthChecks` and `MapHealthChecks`, and requires the custom
`FoundationHealthEndpoints.cs` file to be absent.

- [ ] **Step 2: run RED**

Run:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter "FullyQualifiedName~ApiHostPlatformTests|FullyQualifiedName~HealthEndpointTests" -v q
dotnet test tests/public/Vinx.Erp.ArchitectureTests --filter FullyQualifiedName~NativeHostInfrastructureTests -v q
```

Expected: FAIL for missing OpenAPI, Problem Details, limiter and native health
source; the old health body alone may already pass.

- [ ] **Step 3: replace the custom health endpoint**

In every host register and map:

```csharp
builder.Services.AddHealthChecks();
app.MapHealthChecks("/health");
```

Delete `FoundationHealthEndpoints.cs` and anchor the dependency test on
`typeof(FoundationDbContext).Assembly`.

- [ ] **Step 4: add narrow API error and contract infrastructure**

Register `AddProblemDetails`, `AddOpenApi` and the native limiter. Compose the
pipeline as correlation, exception handling, routing, authentication, limiter,
internal session gate and authorization. Keep `UseStatusCodePages` absent.

```csharp
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    SuppressDiagnosticsCallback = _ => false
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
```

Map `/foundation` through one authorized, rate-limited route group so repeated
`RequireAuthorization()` calls disappear without changing route paths.

- [ ] **Step 5: run GREEN**

Run the same focused commands from Step 2 plus:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter FullyQualifiedName~IdentityGateEndpointTests -v q
```

Expected: PASS; existing 401 bodies remain empty and existing `{ "code": ... }`
responses remain unchanged.

- [ ] **Step 6: commit**

```bash
git add Directory.Packages.props src/Foundation/Vinx.Erp.Foundation src/Hosts/Vinx.Erp.ApiHost src/Hosts/Vinx.Erp.WorkerHost tests/public/Vinx.Erp.Foundation.IntegrationTests tests/public/Vinx.Erp.ArchitectureTests
git commit -m "refactor(api): adopt native HTTP infrastructure"
```

### Task 3: MCP oficial mínimo e fail-closed

**Files:**

- Modify: `Directory.Packages.props`
- Modify: `Vinx.Erp.sln`
- Modify: `src/Hosts/Vinx.Erp.McpHost/Vinx.Erp.McpHost.csproj`
- Modify: `src/Hosts/Vinx.Erp.McpHost/Program.cs`
- Create: `src/Hosts/Vinx.Erp.McpHost/McpPingTools.cs`
- Create: `src/Hosts/Vinx.Erp.McpHost/appsettings.json`
- Create: `tests/public/Vinx.Erp.McpHost.IntegrationTests/Vinx.Erp.McpHost.IntegrationTests.csproj`
- Create: `tests/public/Vinx.Erp.McpHost.IntegrationTests/McpHostTests.cs`
- Create generated lock:
  `tests/public/Vinx.Erp.McpHost.IntegrationTests/packages.lock.json`
- Modify generated locks for McpHost/Foundation through restore

**Interfaces:**

- Consumes: `AddSupabaseJwtAuthentication(IConfiguration)` from Task 1.
- Produces: `POST /mcp` using official stateless Streamable HTTP.
- Produces: MCP tool `vinx_ping` with empty input and text output `pong`.
- Produces annotations `ReadOnly=true`, `Destructive=false`,
  `Idempotent=true`, `OpenWorld=false`.

- [ ] **Step 1: add the public test project and write failing tests**

Add explicit test-client package `ModelContextProtocol` 2.2.0 and use
`HttpClientTransport`, not handwritten JSON-RPC, for the successful flow:

```csharp
await using var client = await McpClient.CreateAsync(
    new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
        TransportMode = HttpTransportMode.StreamableHttp,
        EnableStandaloneGetStream = false,
        AdditionalHeaders = new Dictionary<string, string>
        {
            ["X-Test-Subject"] = "actor-a"
        }
    }, httpClient),
    new McpClientOptions { ProtocolVersion = "2026-07-28" });

var tool = Assert.Single(await client.ListToolsAsync());
Assert.Equal("vinx_ping", tool.Name);
var result = await client.CallToolAsync("vinx_ping");
Assert.Equal("pong", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
```

Also assert: unauthenticated POST is 401; disallowed `Host` is rejected; present
`Origin` is 403; 61 rapid protected requests reach 429; `/health` remains 200;
the MCP client has no session ID; only one tool is listed.

- [ ] **Step 2: run RED**

Run:

```bash
dotnet test tests/public/Vinx.Erp.McpHost.IntegrationTests -v q
```

Expected: FAIL because `/mcp` and `vinx_ping` do not exist.

- [ ] **Step 3: add official packages and minimal server**

Pin `ModelContextProtocol` and `ModelContextProtocol.AspNetCore` at 2.2.0.
Register exactly one generic tool type:

```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
        options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<McpPingTools>();

app.MapMcp("/mcp")
    .RequireAuthorization()
    .RequireRateLimiting("authenticated");
```

`McpPingTools` is a sealed non-static type with a static attributed method, so
generic registration works without assembly scanning or DI state.

- [ ] **Step 4: enforce transport-level basic security**

Set MCP `AllowedHosts` to `localhost;127.0.0.1;[::1]`. Add one path-scoped
middleware before authentication that rejects every present `Origin` with 403.
Do not add permissive CORS or advertise MCP OAuth metadata.

- [ ] **Step 5: run GREEN and package audit**

Run:

```bash
dotnet test tests/public/Vinx.Erp.McpHost.IntegrationTests -v q
dotnet list Vinx.Erp.sln package --vulnerable --include-transitive
```

Expected: all MCP tests PASS and no new vulnerable package is reported.

- [ ] **Step 6: commit**

```bash
git add Directory.Packages.props Vinx.Erp.sln src/Hosts/Vinx.Erp.McpHost tests/public/Vinx.Erp.McpHost.IntegrationTests
git commit -m "feat(mcp): add authenticated SDK transport probe"
```

### Task 4: Contratos, evidência e publicação da PR 207

**Files:**

- Create: `docs/verification/backend-native-mcp-baseline-test-cases.md`
- Modify: `docs/modules/foundation.md`
- Modify: `docs/contracts/mcp/README.md`
- Modify: `docs/handoffs/2026-08-15-backend-api-fe-priorities.md`
- Modify: `docs/superpowers/plans/2026-08-15-backend-first-simplification.md`
- Modify generated lockfiles only through restore

**Interfaces:**

- Produces: concise BDD-to-test manifest for every scenario in the approved
  spec.
- Produces: handoff stating that native baseline and MCP probe are complete,
  while #133/#132/#125 remain required before business tools.

- [ ] **Step 1: update durable docs and verification manifest**

Map success, validation, access, state and concurrency scenarios to exact test
methods. State explicitly that `vinx_ping` is a transport probe, not dependency
readiness and not completion of #133.

- [ ] **Step 2: restore and calculate the real diff**

Run:

```bash
dotnet restore Vinx.Erp.sln
git diff --stat origin/main...HEAD
git diff --numstat origin/main...HEAD
git diff --check
```

Separate generated lockfile lines from manual lines in the PR body.

- [ ] **Step 3: run focused and complete public verification**

Run:

```bash
dotnet format whitespace --verify-no-changes
dotnet build --no-restore
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests --no-build -v q
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --no-build --filter 'Category!=SupabaseStorage' -v q
dotnet test tests/public/Vinx.Erp.McpHost.IntegrationTests --no-build -v q
dotnet test tests/public/Vinx.Erp.ArchitectureTests --no-build -v q
./scripts/verify-public.sh
```

Expected: every focused suite and the complete public gate pass with no warning
or formatting drift.

- [ ] **Step 4: independent critical review**

Ask one independent reviewer to inspect `origin/main...HEAD` for: secret leaks,
Options fail-fast behavior, JWT invariants, empty 401 contracts, audit ordering,
Problem Details leakage, limiter cardinality, Host/Origin enforcement, MCP
statelessness and accidental domain access. Correct confirmed findings and
rerun affected tests.

- [ ] **Step 5: commit docs, push and update the existing draft PR**

```bash
git add docs
git commit -m "docs: record native backend MCP baseline"
git push origin codex/backend-simplification
```

Update PR 207 title/body with delivered scope, exclusions, real size, package
versions, public verification and explicit note that #133 remains open. Do not
merge the PR.
