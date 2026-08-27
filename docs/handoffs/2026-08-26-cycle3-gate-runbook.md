# GATE RUNBOOK — ciclo 3 Q1/Q3, branch única `codex/q1-cycle3-p1-p3`

Gerado: 2026-08-26, pelo consolidation workhorse (pós-consolidação das lanes P1 e P3 em branch única).
Destino: agente com janela livre de Docker/Colima executa TODOS os gates abaixo e anexa evidência em `GATE-EVIDENCE.md` na raiz da branch.
**Não versionar em main — artefato do ciclo** (viaja APENAS dentro desta branch de verificação).

Estado de partida: P1 (Paguai comprovante+produtor) e P3 (Demandas 4-slice) JÁ estão mesclados em
`codex/q1-cycle3-p1-p3` (base `origin/main` = `bee80dc`, merges `--no-ff` na ordem p1a→p1b→p3a→p3b→p3c→p3-web,
mais 1 commit de regen de lockfiles). NÃO existe mais seção de ordem de merge — a realidade é branch única.
Já provado sem Docker nesta branch: `dotnet build Vinx.Erp.sln` 0 err/0 warn; units Demandas 27/27;
`tsc -b` e `biome check` nos lane-files web verdes; `dotnet restore --locked-mode` limpo; audits de
grep (telegram_inbound_scheduled ×1 no grant script, AdminGrantNames=5, zero timestamps duplicados,
zero risco 42710 em CREATE POLICY) zerados. O que falta é EXCLUSIVAMENTE o que exige Postgres/DB tooling.

## 0. PASSO ZERO — sanidade de modelo e migrações (antes de qualquer suíte)

1. **Snapshot regen**: rodar `scripts/generate-foundation-schema-snapshot.sh` (ou `dotnet ef migrations add` probe
   em worktree limpa) e conferir drift ZERO contra `FoundationDbContextModelSnapshot.cs` consolidado.
   O snapshot foi unido textualmente (hunks disjuntos: coluna proof do Paguai + tabelas Demandas) e compilou,
   mas regen é a única prova de ausência de drift. SE houver drift: aplicar via script oficial (`--write`),
   commitar na branch e rerodar este passo.
2. **[Migration]-attribute sanity**: cada migração nova (`20260826160000_AddDemandasCore`,
   `20260826233229_RecordDemandasCoreModel`, `20260827090000_AddPaguaiRequestProofFile`,
   `20260828090000_AddPaguaiApproveCapabilityAndProducer`) precisa do atributo `[Migration("...")]`
   no Designer (já auditado nesta branch — reconfirmar barato com grep) E aparecer em
   `dotnet ef migrations list`. Migração sem atributo some do histórico (aconteceu na lane P1).
3. **Apply completo**: `dotnet ef database update` (ou fixture `MigrateAsync`) contra Postgres descartável
   LIMPO (`postgres:17.9-alpine` ou imagem da fixture Foundation) aplicando o histórico INTEIRO sem erro —
   prova a reestruturação 3abc501 (cross-chunk role-state) e as duas famílias novas de política/fila.

## 1. REGRAS DE EXECUÇÃO DO GATE (obrigatórias)

1. Protocolo de propriedade Docker: seguir `docs/runbooks/public-verification.md` linhas 75–113
   (DOCKER_CONTEXT, pgrep PID/CWD, label testcontainers, limpeza só do próprio). Stack `supabase_*`
   do vinx-erp-groundwork NUNCA é tocada; containers/processos alheios nunca mortos.
2. Suítes SERIALIZADAS (uma por vez). Worktree desta branch já restaurada em locked-mode; se algo
   mexer em lockfile: `dotnet restore --locked-mode` antes. Web: `npm ci` uma vez antes de qualquer
   check FE (playwright install só se rodar e2e — não planejado).
3. Falha ⇒ corrigir DENTRO desta branch e rerodar o filtro (máx 3 ciclos por suíte; senão PARAR e
   reportar estado exato). Falha pré-existente fora dos filtros = documentar como fato da base.
4. Base pode ter andado desde `bee80dc`: antes de tudo `git fetch origin && git log --oneline -5 origin/main`;
   se main mudou, reportar (merge-base a refazer — decisão do orquestrador).
5. Nunca push; publicação de PR fica para o orquestrador/responsável após gates verdes.

## 2. LANE P1 (Paguai comprovante+produtor) — filtros exatos

DOIS DEFEITOS VIVOS isolados na pausa do gate anterior — resolver PRIMEIRO (capturar evidência antes
de corrigir, ver seção ARMADILHAS):
- `FoundationSchemaTests.Migrator_can_rollback`: `42501` em schema paguai durante rollback-test;
  statement negador exato AINDA NÃO capturado — capturar antes de corrigir.
- `PaguaiApproverAccess`: precisa da sessão trocada para `WorkerConnectionString` antes de rerodar
  (probe comportamental exige identidade do worker).
Todos os verdes anteriores à pausa são STALE (predatam as edições finais da migração) ⇒ rerun completo.

Ordem:
1. Filtros de integração (todos DEVEM ficar 100% verdes):
   - `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter FullyQualifiedName~PaguaiDraftNotificationTests`
   - `--filter FullyQualifiedName~CompanyDefaultRolesMigrationTests` (pins: admin=5 incluindo
     `paguai.draft.approve`; producer map `["foundation.files.upload","paguai.draft.approve"]`)
   - `--filter FullyQualifiedName~WolverineStorageScriptTests` (fila `wolverine_queue_paguai_events(_scheduled)`
     provisionada; loop de grants com limite EXATO `index <= 16` — não alterar)
   - `--filter FullyQualifiedName~PaguaiProofBindingEndpointTests` (rerun de confirmação; era 11/11)
2. Rollback-hygiene manual 1×: banco descartável, `database update` uma versão atrás e reaplicar
   (prova Down simétrico do provisionador; nenhuma suíte cobre Down).

## 3. LANE P3 (Demandas 4-slice) — filtros exatos

Primeira janela desta lane (nada executado por design RED-by-gate; units 27/27 já verdes na branch).
Ordem sugerida:
1. `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter FullyQualifiedName~Demandas`
   (lifecycle create→route→advance→approval→close, re-initiation, version conflicts, RLS cross-company,
   outbox intents, worker replay idempotente, pagination envelope).
2. `--filter FullyQualifiedName~WorkerHostSecurityContractTests` (pins SQL round-3: grants 4..19 True,
   cauda False20/True21/False22 — ÍNDICES EXATOS, não alterar) e `--filter FullyQualifiedName~ApprovalContractsTests`
   (byte-stability legacy ai.*).
3. `--filter FullyQualifiedName~FoundationSchemaTests` (catálogo dinâmico: capabilities demandas.* + a
   coluna proof do Paguai; inclui o caso `Migrator_can_rollback` do passo P1 — mesma suíte).
4. Worker de automação: prova mínima end-to-end do tick (fila `demandas-automation` materializada,
   handler auto-perpetuando, DUE primeiro/overdue depois) DENTRO das fixtures existentes do WorkerHost —
   não inventar harness novo.
5. Web: `npm --workspace apps/web run test -- --run features/demandas` (board/paginação/deep-link/chips;
   era 7/7 na lane) — vitest é permitido NESTE gate (a cerca compile-only era só do consolidador).

## 4. ARMADILHAS CONHECIDAS DESTAS LANES (re-checar após qualquer fix)

- Toda function SECURITY DEFINER nova exige `set_config('app.company_id',…,true)` interno + OWNER correto
  via GRANT transitivo→SET ROLE→CREATE→RESET→REVOKE + grants USAGE/SELECT ao role dono + policy visível
  ANTES do uso em DB vazio (precedentes `20260812040429`, `20260825110000`; bugs reais 2× nestas lanes).
- Fila Wolverine nova precisa entrar em `infra/database/grant-wolverine-runtime.sql` + pins de teste
  (hosts AutoCreate.None — fila não materializa sozinha). Na branch consolidada o script tem as duas
  famílias (`paguai_events` + `demandas_automation`) e `telegram_inbound_scheduled` UMA vez só.
- Pins de catálogo admin mudam count com capability nova (4→5 com `paguai.draft.approve`);
  UUIDv5 canônico `uuid5(URL,'https://vinx.erp/capabilities/<name>')`.
- Dedupe de intents por event key: contadores de ciclo/ocorrência precisam entrar na key quando eventos
  legítimos repetem (veto incrementa StageCycleCount antes dos eventos).
- Migrações: par 231302↔233229 já resolvido para `20260826233229_RecordDemandasCoreModel` (rename da p3b
  aplicado limpo). Se algum resíduo `231302` aparecer em histórico EF, é ressurreição de merge — reportar.

## 5. FORMATO DE EVIDÊNCIA

Criar/anexar em `GATE-EVIDENCE.md` NA RAIZ DA BRANCH (commitar na branch junto com fixes, se houver):
para cada passo — comando literal + exit code + contagens exatas (passed/failed/skipped) + timestamp.
Exemplo:
```
## 2026-08-27 14:02 — Demandas integration
$ dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter FullyQualifiedName~Demandas
exit=0  Passed! Failed:0 Passed:34 Skipped:0
```

## 6. DEFINITION OF DONE (branch única)

(a) passo zero 1–3 verdes (snapshot drift zero ou commitado; migrations list completa; apply integral);
(b) TODOS os filtros das seções 2–3 verdes com evidência no `GATE-EVIDENCE.md`;
(c) zero objetos Docker fora da isolation; (d) `git diff --stat origin/main..HEAD` revisado por cima
antes de declarar pronto; (e) avisar o orquestrador/user para publicar o PR de
`codex/q1-cycle3-p1-p3` (título previsto: `[Ciclo3][Q1+Q3] Paguai comprovante+produtor e Demandas MVP consolidados`).
