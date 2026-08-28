# Main Simplification Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> `superpowers:subagent-driven-development` to execute this plan task-by-task.
> Steps use checkbox (`- [ ]`) syntax for tracking.

**Objetivo:** revisar e simplificar com segurança a base atual, consolidando
somente findings concretos em uma PR única.

**Arquitetura:** manter o monólito modular .NET 10, os três hosts ASP.NET Core e
o frontend React/Vite. A revisão é paralela e somente de leitura; a triagem e a
escrita são serializadas por um Terra, com Lunas para blocos independentes.

**Stack:** .NET 10, ASP.NET Core, EF Core/Npgsql, Wolverine/PostgreSQL, React 19,
TypeScript, Vite, Vitest, Playwright e GitHub Actions.

## Restrições globais

- Não ler, listar ou executar `quality/private`.
- Não quebrar autorização, contexto de empresa, auditoria, observabilidade,
  idempotência, concorrência, entrega externa, MCP, OpenAPI ou RLS.
- Não criar dependência ou abstração sem uso atual e motivo documentado.
- TDD: teste falhando antes de cada alteração executável; depois, teste focado
  e consumidor direto necessário.
- Não rodar loops de gate completo; `./scripts/verify-public.sh` no máximo uma
  vez após o congelamento, se o cruzamento de projetos ou a regra do repositório
  exigir.
- Documentação nova somente para decisão durável ou contrato verificável;
  relatórios de agentes ficam fora do Git.

**Review report contract:** every lane writes a Markdown report under the
ignored SDD workspace with one row per finding: `ID`, severity, exact path and
symbol, evidence from current code/call sites, KISS/DRY/SOLID/YAGNI dimension,
smallest safe change, behavior to preserve, focused test, and disposition.
"Adopt", "defer", and "reject" are the only triage dispositions.

---

### Tarefa 1: inventário e ledger de revisão

**Arquivos:**
- Ler: `README.md`, `AGENTS.md`, desenho de arquitetura, módulos e runbooks
  afetados.
- Criar: `.superpowers/sdd/main-simplification-cleanup/progress.md` (ignorado).

**Passos:**

- [ ] Registrar SHA/base, branch, estado limpo, SDKs, projetos e contagem de
  arquivos/linhas with `git rev-parse`, `git status --short`, `dotnet --version`,
  `node --version`, `dotnet sln Vinx.Erp.sln list`, and `wc -l` over tracked
  public paths.
- [ ] Mapear cada review lane to the affected public test project and the
  smallest command listed in `docs/runbooks/public-verification.md`.
- [ ] Confirm Docker context, active VINX test processes, and owned containers
  with the runbook inventory before any integration or full-gate command.

### Tarefa 2: revisão paralela por blocos

**Arquivos:**
- Ler somente os caminhos do bloco e consumidores necessários.
- Criar somente relatórios em `.superpowers/sdd/main-simplification-cleanup/`.

**Passos:**

- [ ] Luna Foundation/identity/database: procurar duplicação, código morto,
  abstrações e complexidade sem tocar no código.
- [ ] Luna AI/document pipeline: verificar providers, guardrails, OCR e
  contratos de contexto sem tocar no código.
- [ ] Luna modules/hosts: verificar Paguai, Demandas, Atendimento,
  Notifications, Api/Mcp/Worker e setup compartilhado sem tocar no código.
- [ ] Luna frontend: verificar componentes, clientes BFF, hooks, estado,
  testes e CSS sem tocar no código.
- [ ] Luna infra/tests/docs: verificar scripts, CI, dependências, fixtures,
  documentação redundante e overtesting sem tocar no código.
- [ ] ZCode manter o relatório independente completo antes da triagem.

Cada relatório deve conter somente findings reproduzíveis com localização,
regra afetada, severidade, proposta mínima, risco de regressão e teste que
provaria a mudança; pontos especulativos devem ser marcados como adiados.

The read-only lanes must not modify tracked files, start Docker, run the full
gate, or inspect `quality/private`; their evidence comes from current source,
public tests, tracked docs, and call-site searches.

### Tarefa 3: triagem e seleção da onda de correção

**Arquivos:**
- Ler: todos os relatórios da Tarefa 2.
- Criar: `.superpowers/sdd/main-simplification-cleanup/triage.md`.

**Passos:**

- [ ] Terra reconciliar duplicatas e validar call sites/testes dos findings.
- [ ] Terra separar `adotar`, `adiar` e `rejeitar`, explicando o motivo de cada
  decisão e calculando arquivos/linhas da onda adotada.
- [ ] Se um finding exigir mudança de contrato público, banco, segurança ou
  integração, incluir sua prova mínima e excluir melhorias não relacionadas.
- [ ] Congelar a lista adotada antes de editar produção.

The triage report records the exact baseline SHA, every report consumed, each
finding disposition, rejected/deferred rationale, selected files, manual-line
estimate, test matrix, and the single writer order used by Task 4.

### Tarefa 4: correção TDD gerenciada pelo Terra

**Arquivos:** exatamente os paths aprovados em `triage.md`, seus testes e a
documentação de contrato realmente afetada.

**Passos:**

- [ ] Terra dividir a lista congelada em blocos com conjuntos de escrita
  disjuntos e delegar Lunas apenas quando houver ganho real.
- [ ] Para cada bloco, escrever um teste RED que falhe pela causa indicada e
  executar o filtro mínimo.
- [ ] Implementar a menor remoção/consolidação que produz GREEN, sem refatorar
  áreas adjacentes.
- [ ] Executar o teste focal, o projeto afetado e consumidores transitivos
  necessários; registrar comando e resultado no relatório.
- [ ] Atualizar somente a documentação que ficou stale e manter o diff-check.
- [ ] Terra integrar serialmente os blocos, resolver conflitos e manter um único
  conjunto de commits coerente para a PR.

For each adopted executable finding, the Luna implementer must leave this
evidence in its report: the failing test command and failure, the minimal code
change, the passing focused command, and the affected-project command. A
documentation-only item uses `git diff --check` and the directly consuming
contract check instead of an invented behavior test.

### Tarefa 5: revisão independente e fechamento

**Arquivos:** diff completo da branch e artefatos do ledger.

**Passos:**

- [ ] Solicitar uma revisão independente do diff consolidado com foco em
  simplicidade, overengineering, segurança e regressão.
- [ ] Pedir a revisão final do GLM-5.3 não-Flash no nível máximo e permitir uma
  única correção TDD adicional se ele encontrar um blocker confirmado.
- [ ] Congelar escopo e executar os testes finais proporcionais; executar o
  gate público uma vez se necessário.
- [ ] Verificar tamanho, `git diff --check`, branch/ancestry, push, PR head/base,
  checks, reviews e mergeability no remoto.
- [ ] Criar uma única PR contra `main`; não fazer merge local nem tocar em
  `main`.

The final review package includes the merge-base-to-HEAD diff, the triage and
ledger paths, and every deferred/rejected item. Residual Critical/Important
findings are fixed in one consolidated wave or reported as blockers; they are
not silently discarded.
