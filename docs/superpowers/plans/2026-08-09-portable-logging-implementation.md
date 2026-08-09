# Logging portátil Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Configurar logs JSON estruturados e correlação HTTP reutilizável nos hosts VINX por meio das abstrações nativas do .NET.

**Architecture:** `Foundation.Observability` concentra a composição de `ILogger`, scopes e activity tracking. Os hosts chamam extensões pequenas; módulos continuam consumindo apenas `ILogger<T>`, portanto trocar provider exige alterar a composição, não os serviços de domínio.

**Tech Stack:** .NET 10, ASP.NET Core, Microsoft.Extensions.Logging, OpenTelemetry já referenciado e xUnit.

## Global Constraints

- Não adicionar provider, adapter, pacote ou `IVinxLogger` próprio.
- Usar somente `ILogger<T>` e DI nativos.
- JSON console com timestamp UTC, scopes, `TraceId` e `SpanId`; não incluir `Tags` ou `Baggage`.
- `CorrelationId` é GUID do servidor; nenhum log por requisição.
- Não ler, listar nem executar `quality/private`.
- Testes públicos primeiro; executar `./scripts/verify-public.sh` uma vez antes do push.

---

### Task 1: Configuração compartilhada e correlação HTTP

**Files:**
- Create: `src/Foundation/Vinx.Erp.Foundation/Observability/VinxLoggingExtensions.cs`
- Create: `src/Foundation/Vinx.Erp.Foundation/Observability/RequestCorrelationMiddleware.cs`
- Create: `tests/public/Vinx.Erp.Foundation.UnitTests/Observability/LoggingInfrastructureTests.cs`
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Program.cs`
- Modify: `src/Hosts/Vinx.Erp.McpHost/Program.cs`
- Modify: `src/Hosts/Vinx.Erp.WorkerHost/Program.cs`
- Modify: `docs/modules/foundation.md`

**Consumes:** `ServerCorrelationContext.Begin` e `CorrelationContext` existentes.

**Produces:** `AddVinxLogging(ILoggingBuilder)` e `UseVinxRequestCorrelation(IApplicationBuilder)` para todos os hosts.

- [ ] **Step 1: Escrever testes RED**

Criar testes que instanciem um host real com `AddVinxLogging` e comprovem um
único `ConsoleLoggerProvider`, formatter JSON e activity tracking com
`TraceId | SpanId`, sem `Tags | Baggage`. Criar pipeline de `RequestCorrelationMiddleware` com provider de captura apenas no teste e comprovar que o log downstream recebe scope contendo somente `CorrelationId`, que o valor é GUID e que o scope termina após `next`.

- [ ] **Step 2: Executar RED**

Run: `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --filter "FullyQualifiedName~LoggingInfrastructureTests"`

Expected: FAIL porque extensões e middleware ainda não existem.

- [ ] **Step 3: Implementar mínimo GREEN**

Implementar `AddVinxLogging` com `AddJsonConsole`, scopes, UTC, JSON e
`LoggerFactoryOptions.ActivityTrackingOptions = TraceId | SpanId`.
Implementar middleware que inicia `ServerCorrelationContext`, marca o
`Activity`, abre scope com `CorrelationId` e chama `next` sem logar.
Substituir middleware inline da API, aplicar middleware no MCP e aplicar a
configuração compartilhada nos três `Program.cs`. Documentar fronteira e
dados proibidos em `docs/modules/foundation.md`.

- [ ] **Step 4: Executar GREEN focado**

Run: `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --filter "FullyQualifiedName~LoggingInfrastructureTests"`

Expected: PASS, sem aviso.

- [ ] **Step 5: Verificar hosts e commit**

Run: `dotnet build Vinx.Erp.sln --no-restore && git diff --check`

Commit: `feat(observability): configurar logs portáveis`.
