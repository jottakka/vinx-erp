# Project Map - VINX ERP

Status: mapa de piloto. Git e Markdown versionado são canônicos; `.projectmem`
é memória operacional opcional de desenvolvimento e não entra no produto.

## Project purpose

VINX ERP é um monólito modular .NET com hosts de API, MCP e worker, PostgreSQL
e uma aplicação web React. A base inicial concentra contratos Foundation,
identidade, autorização, auditoria, observabilidade e limites de provedor.

## Structure

- `src/Foundation/Vinx.Erp.Foundation/` — domínio e contratos transversais.
  - `Domain/` — entidades e contratos Foundation.
  - `Authorization/` — capacidades, atribuição e decisões de autorização.
  - `Persistence/` — DbContext, configurações e migrations EF Core.
  - `Audit/` — eventos e append transacional de auditoria.
  - `Observability/` — correlação, logs e telemetria.
- `src/Hosts/` — transportes finos para API, MCP e worker.
  - `Vinx.Erp.ApiHost/Program.cs` — entrada HTTP.
  - `Vinx.Erp.McpHost/Program.cs` — entrada MCP do produto.
  - `Vinx.Erp.WorkerHost/Program.cs` — entrada de trabalhos assíncronos.
- `apps/web/src/` — frontend React; acessa o ERP por API.
- `docs/` — ADRs, contratos, módulos, runbooks, verificações e histórico.
  - `docs/architecture/2026-08-08-vinx-erp-architecture-design.md` — limites arquiteturais.
  - `docs/runbooks/basic-memory.md` — índice local descartável de documentação.
- `tests/public/` — testes públicos de domínio, arquitetura, BDD e integração.
- `scripts/verify-public.sh` — gate público de construção e verificação.
- `.projectmem/` — resumo, plano, mapa e configuração da memória operacional do piloto.

## Relationships

- docs/runbooks/github-memory-import.md points to live GitHub issues without
  duplicating their bodies.
- docs/runbooks/basic-memory.md indexes only the current checkout's docs
  directory and remains disposable.

- `apps/web/src/` chama somente `Vinx.Erp.ApiHost` para dados de negócio.
- `Vinx.Erp.ApiHost`, `Vinx.Erp.McpHost` e `Vinx.Erp.WorkerHost` reutilizam módulos e contratos Foundation.
- `FoundationDbContext` e migrations definem a persistência PostgreSQL canônica.
- `docs/` define limites duráveis; Basic Memory e projectmem apenas aceleram recuperação operacional.
- `.projectmem/summary.md` resume eventos locais e nunca autoriza mudanças no produto ou substitui ADRs.
