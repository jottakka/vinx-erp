# Logging portátil — plano de implementação

> **Para agentes:** SUBSKILL OBRIGATÓRIA: use `superpowers:subagent-driven-development` (recomendada) ou `superpowers:executing-plans` para implementar este plano tarefa a tarefa. As etapas usam checkbox (`- [ ]`) para acompanhamento.

**Objetivo:** Configurar logs JSON estruturados e correlação HTTP reutilizável nos hosts VINX por meio das abstrações nativas do .NET.

**Arquitetura:** `Foundation.Observability` concentra a composição de `ILogger`, scopes e activity tracking. Os hosts chamam extensões pequenas; módulos continuam consumindo apenas `ILogger<T>`, portanto trocar provider exige alterar a composição, não os serviços de domínio.

**Pilha tecnológica:** .NET 10, ASP.NET Core, Microsoft.Extensions.Logging, OpenTelemetry já referenciado e xUnit.

## Restrições globais

- Não adicionar provider externo ou próprio, adapter, pacote ou `IVinxLogger`.
- Usar somente `ILogger<T>` e DI nativos.
- JSON console com `TimestampFormat = "O"`, UTC, scopes, `TraceId` e `SpanId`; não incluir `Tags` ou `Baggage`.
- `CorrelationId` é GUID do servidor; nenhum log por requisição.
- Filtrar `Microsoft.AspNetCore` em `Warning`, mantendo filtros restantes configuráveis pela seção `Logging`.
- Não ler, listar nem executar `quality/private`.
- Testes públicos primeiro; executar `./scripts/verify-public.sh` uma vez antes do push.

---

### Task 1: Configuração compartilhada e correlação HTTP

**Arquivos:**
- Create: `src/Foundation/Vinx.Erp.Foundation/Observability/VinxLoggingExtensions.cs`
- Create: `src/Foundation/Vinx.Erp.Foundation/Observability/RequestCorrelationMiddleware.cs`
- Create: `tests/public/Vinx.Erp.Foundation.UnitTests/Observability/LoggingInfrastructureTests.cs`
- Create: `docs/verification/portable-logging-test-cases.md`
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Program.cs`
- Modify: `src/Hosts/Vinx.Erp.McpHost/Program.cs`
- Modify: `src/Hosts/Vinx.Erp.WorkerHost/Program.cs`
- Modify: `docs/modules/foundation.md`

**Consome:** `ServerCorrelationContext.Begin` e `CorrelationContext` existentes.

**Produz:** `AddVinxLogging(ILoggingBuilder)` e `UseVinxRequestCorrelation(IApplicationBuilder)` para todos os hosts.

**Estimativa:** 5 arquivos de produção, 1 de teste e 4 de documentação;
275–365 linhas manuais, sem pacote, migration, lockfile ou CI. Classes de
cobertura: sucesso (JSON, idempotência e scope), validação/erros (filtro e
timestamp UTC), acesso (N/A: nenhum endpoint ou decisão de acesso), estado e
exceções (descarte de scope), concorrência (isolamento entre requisições).
Integração, BDD, arquitetura, web e teste manual são N/A: não há persistência,
linguagem de negócio, regra estrutural nova, UI nem operação externa nesta
fatia.

- [ ] **Etapa 1: Escrever testes RED**

Criar testes que instanciem um host real com `AddVinxLogging` duas vezes e
comprovem um único `ConsoleLoggerProvider`, formatter JSON, `Timestamp` ISO
8601 em UTC e activity tracking com `TraceId | SpanId`, sem
`Tags | Baggage`. Comprovar que logger da categoria
`Microsoft.AspNetCore.Hosting.Diagnostics` não habilita `Information`.
Criar pipeline de `RequestCorrelationMiddleware` com provider de captura
apenas no teste e comprovar que o log downstream recebe scope contendo
somente `CorrelationId`, que o valor é GUID, que duas requisições
concorrentes não compartilham valores e que o scope termina mesmo quando
`next` lança exceção. Separar o scope próprio do scope automático de
`TraceId`/`SpanId` na asserção.

- [ ] **Etapa 2: Executar RED**

Executar: `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --filter "FullyQualifiedName~LoggingInfrastructureTests"`

Esperado: FALHA porque extensões e middleware ainda não existem.

- [ ] **Etapa 3: Implementar mínimo GREEN**

Implementar `AddVinxLogging` com `AddJsonConsole`, scopes,
`TimestampFormat = "O"`, UTC, filtro `Microsoft.AspNetCore = Warning` e
`LoggerFactoryOptions.ActivityTrackingOptions = TraceId | SpanId`.
Implementar middleware que inicia `ServerCorrelationContext`, marca o
`Activity`, abre scope com `CorrelationId` e chama `next` sem logar.
Substituir middleware inline da API, aplicar middleware no MCP e aplicar a
configuração compartilhada nos três `Program.cs`. Documentar fronteira e
dados proibidos em `docs/modules/foundation.md`; criar o contrato de casos e
comandos em `docs/verification/portable-logging-test-cases.md`.

- [ ] **Etapa 4: Executar GREEN focado**

Executar: `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --filter "FullyQualifiedName~LoggingInfrastructureTests"`

Esperado: PASSA, sem aviso.

- [ ] **Etapa 5: Verificar hosts e commit**

Executar: `dotnet build Vinx.Erp.sln --no-restore && git diff --check`

Commit: `feat(observability): configurar logs portáveis`.

- [ ] **Etapa 6: Executar gate público antes do push**

Executar: `./scripts/verify-public.sh`

Esperado: PASSA. Não executar novamente nesta branch antes do push.
