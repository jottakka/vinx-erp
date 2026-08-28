# Deferred Findings Cleanup (pós-PR #359) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolver em uma única PR os findings deferidos pela revisão de simplicidade (#359) que têm correção segura e prova proporcional no HEAD atual (ea94fe2b).

**Architecture:** Fatia única de cleanup por mandato explícito do dono ("em um único PR"), excedendo o alvo de 800 linhas com justificativa registrada. Cada finding é um commit independente e revertável. Revalidação feita por 4 agentes de leitura contra o HEAD; todos os findings abaixo continuam presentes.

**Tech Stack:** .NET 10 (Foundation modules, Wolverine, EF Core, Testcontainers), React/TypeScript (apps/web, vitest), bash scripts.

## Global Constraints

- Nunca ler, listar nem executar `quality/private`.
- Nada vai direto à `main`; branch `codex/deferred-findings-cleanup` (já criada off origin/main ea94fe2b).
- Preservar autorização, empresa explícita, RLS, auditoria, idempotência, concorrência, outbox, DLQ, provider, contratos OpenAPI/MCP.
- `./scripts/verify-public.sh` no máximo UMA vez, após congelar escopo (AGENTS.md; mudança atravessa >1 projeto público + segurança).
- Testes Docker serializados; não tocar em worktrees/stacks de outros processos.
- Documentação em português; sem segredos/PII/prompts em logs, fixtures ou Markdown.
- Findings deliberadamente FORA desta PR (registrar no PR): AI-03 (remover fachada DeepSeek desfaria feature entregue #135-A), ZC-3/ZC-4/ZC-5 (DRY de alto risco — "uma operação por vez"), ZC-8 (decisão de produto/aparência), ZC-7/ZC-11 (rejeitados na revisão), ZC-10 (estado de billing do GitHub — só relatar), INFRA-002 (compose.dev.yml mantido e documentado como contrato de rede compartilhada da stack local Supabase).

---

### Task 1: FND-02 — Down() da migration AddDocumentCore

**Files:**
- Modify: `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260810013145_AddDocumentCore.cs:184-185`
- Test: `tests/public/Vinx.Erp.Foundation.IntegrationTests/` (novo `DocumentCoreMigrationDowngradeTests.cs`, padrão `AuditMigrationTests.cs:20,80`)

**Interfaces:** usa `IMigrator.Database.MigrateAsync("<id anterior>")` para executar Down real em PostgreSQL (Testcontainer).

- [ ] Down SQL: `UPDATE foundation.files SET is_quarantined = processing_state IN ('quarantined','verification_failed')` — `confirmed` e `verified` descem como `is_quarantined = false` (estado liberado no schema antigo).
- [ ] Teste RED→GREEN: semear `quarantined`, `verified`, `confirmed`, `verification_failed` no schema novo; migrar para baixo; afirmar `is_quarantined` esperado por estado; migrar para cima de novo sem perda de checksum/retenção.
- [ ] Rodar: `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter "FullyQualifiedName~DocumentCoreMigrationDowngrade"` (serializado).

### Task 2: FND-03/04/05 — remover superfícies mortas de Files

**Files:**
- Modify: `src/Foundation/Vinx.Erp.Foundation/Files/FileStorage.cs:52` (remover `ListObjectsAsync` da interface), `SupabaseFileStorage.cs:197`, `BlockedFileStorage.cs:44`, `src/Hosts/Vinx.Erp.WorkerHost/WorkerFileStorage.cs:55-58`
- Modify: `src/Foundation/Vinx.Erp.Foundation/Files/StorageObjectInspection.cs` (remover parâmetro `MalwareScanStatus`), `SupabaseFileStorage.cs:132-138`
- Modify: `src/Foundation/Vinx.Erp.Foundation/Domain/FileRecord.cs:79-81,173-174` (remover `IsQuarantined` e `IsPhysicalDeletionAllowed`), `Persistence/Configurations/FoundationEntityConfigurations.cs:328` (remover `Ignore(IsQuarantined)`)
- Modify: `src/Foundation/Vinx.Erp.Foundation/Approvals/ApprovalCapabilityPolicy.cs:42-43` (remover `FromCapability`), `Approvals/ApprovalContracts.cs:35` (remover `IsValidHash`)
- Tests: atualizar stubs em 6 arquivos de teste (E2eFixtureData, DocumentUploadEndpointTests, TelegramInboundWorkerTests, WorkerAgentEfPathsTests, PaguaiProofEndpointTests, AuthorizedDocumentSourceTests, MetaWhatsAppDocumentInboundTests), `SupabaseFileStorageTests.cs:84,132`, `DocumentVerificationPolicyTests.cs:15-18` (remover teste reflexivo do campo), `FileRecordLifecycleTests.cs:35,47,74,95-100`, `ApprovalContractsTests.cs:50-51,221`
- Se `StorageObjectSummary` ficar órfão após remover `ListObjectsAsync`, remover também.

- [ ] Remover as 5 superfícies sem consumidor de produção (grep confirma: só testes/stubs).
- [ ] Fundação Unit green: `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests`.
- [ ] `dotnet build` da solução para pegar stubs quebrados.

### Task 3: FND-06 — deduplicar validação de MalwareScannerOptions

**Files:**
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Program.cs:200-210`, `src/Hosts/Vinx.Erp.WorkerHost/Program.cs:277-287`
- Ambos passam a usar `options.Validate()` (já existe em `MalwareScannerOptions.cs:20-38`); `ValidateOnStart` preservado; `BlockedMalwareScanner` sem endpoint preservado.

- [ ] Trocar predicado inline por `options.Validate()` nos dois hosts.
- [ ] Teste: options inválidas derrubam startup (contrato existente dos hosts — rodar Hosts Integration filtro MalwareScanner/WorkerHostSecurityContract).

### Task 4: FND-07 — extrair núcleo comum de EmployeeDirectoryService

**Files:**
- Modify: `src/Foundation/Vinx.Erp.Foundation/Employees/EmployeeDirectoryService.cs:127-198`
- Extrair `ListCoreAsync(actor, companyId, CapabilityRequirement requirement, ct)`; `ListAsync` passa `CreateRequirement`, `ListForPickerAsync` passa `ReadRequirement`. Zero mudança de fronteira de autorização (única diferença atual é a requirement).

- [ ] Refactor + Foundation Integration filtro EmployeeDirectory green.

### Task 5: FND-08/CC-004 — 28× `new CorrelationContext(actor.CorrelationId).CorrelationId` → `actor.CorrelationId`

**Files:** 16 arquivos em src/ (lista completa no relatório de revalidação: Approvals x7, Platform x3, Authorization x8, Demandas x3, Permissions x2, Files x1, Employees x1, CapabilityMap x1, Atendimento x1, AI x1). Não tocar `Identity/ActorContext.cs:21` (criação legítima) nem o middleware HTTP.

- [ ] Substituição mecânica; Foundation Unit + build da solução.

### Task 6: AI-02/AI-05 — MaxTotalTokens morto + teste reflexivo

**Files:**
- Modify: `src/Modules/Vinx.Erp.AI/AgentTaskProfile.cs:37,105-108` (remover campo + validação), `AgentTaskProfileCatalog.cs:92,108`, testes `AgentTaskProfileTests.cs`, `AgentRunnerGateIntegrationTests.cs:308,321`
- Modify: `tests/public/Vinx.Erp.AI.UnitTests/AgentRunnerAttachmentTests.cs:211-218` — remover teste reflexivo de `ComposeTurn` (cobertura real de truncagem/surrogate já existe via RunAsync nos testes :123-160 e :231-307).

- [ ] Remoção + AI Unit green.

### Task 7: DEM-001 — relacionamento EF rotina→etapas (insert order + atomicidade)

**Files:**
- Modify: `src/Foundation/Vinx.Erp.Foundation/Persistence/Configurations/DemandasConfigurations.cs:131-152` — `DemandRoutineStageConfiguration`: `HasOne<DemandRoutine>().WithMany().HasForeignKey(CompanyId, RoutineId).HasConstraintName("fk_demand_routine_stages_routine").OnDelete(DeleteBehavior.Restrict).IsRequired()`
- Migration nova com `Up()`/`Down()` VAZIOS (só atualiza snapshot — a FK já existe no banco desde `20260826160000_AddDemandasCore.cs:73-75`).
- Test: `tests/public/Vinx.Erp.Foundation.IntegrationTests/DemandasCoreEndpointTests.cs` — POST `/demandas/routines` com 2+ etapas → 201, todas persistidas, replay idêntico.

- [ ] Declarar relacionamento; migration vazia editada à mão.
- [ ] `./scripts/verify-foundation-database.sh` (snapshot idêntico) + `./scripts/verify-ef-negative-guards.sh`.
- [ ] Teste de endpoint green.

### Task 8: DEM-002 — corrida de idempotência replaya em vez de 503

**Files:**
- Modify: `src/Modules/Vinx.Erp.Demandas/DemandasService.cs` — os 11 catch `IsIdempotencyKeyConflict` (:396,:501,:692,:805,:926,:999,:1114,:1246,:1331,:1492,:1566) trocam `IdempotencyRaceAsync()` (Unavailable) por `ReplayAfterRaceAsync(...)`: reler receipt por `(CompanyId, ActorUserId, CommandName, RequestId)` via `ReplayOrNullAsync` → mesmo `PayloadHash` = Replay com `ResultJson`; hash divergente = estado Conflict (`idempotency_conflict` 409). Remover helper `IdempotencyRaceAsync` (:1725).
- Test: `DemandasCoreEndpointTests.cs` — duas criações idênticas concorrentes (`Task.WhenAll`): exatamente um 201/efeito, o outro replay 2xx, zero 503 (padrão `AtendimentoCardEndpointTests.cs:1496`); requisição divergente com mesmo requestId → 409.

- [ ] Implementar helper + trocar 11 catch blocks.
- [ ] Testes de corrida green (serializado).

### Task 9: DEM-003 — rejeitar topologia `time_elapsed` → `awaiting_approval`

**Files:**
- Modify: `src/Modules/Vinx.Erp.Demandas/DemandasService.cs:1580-1612` (`ValidateRoutineStages`) — se o gatilho da etapa é `TimeElapsed` e a próxima etapa resolvida é `AwaitingApproval` → inválida (`routine_invalid` 400). Decisão conservadora fail-closed: sem ator sintético, sem aprovação actorless.
- Test: criação de rotina com essa topologia → 400; topologia válida vizinha (manual → awaiting_approval) continua aceita.

### Task 10: DEM-004 — respostas de rotina com Stages reais

**Files:**
- Modify: `DemandasService.cs:1483` (criar resposta a partir das `orderedStages` rastreadas), `:1557,:1562` (`UpdateRoutineStatusAsync` carrega etapas como `ListRoutinesAsync:1377-1382` antes de projetar).
- Test: POST retorna as etapas persistidas; mudança de status retorna etapas; GET lista igual.

### Task 11: DEM-005 — AutoAssigneeMembershipId resolve o responsável na criação

**Files:**
- Modify: `DemandasService.cs` `CreateAsync` (:272-354) — quando `ResponsibleMembershipId` ausente e a rotina roteada tem `AutoAssigneeMembershipId`: resolver responsável da rotina, revalidando membership ativa na mesma empresa (senão 400). Ambos ausentes → invalid como hoje. Sem fallback silencioso por nome/cargo.
- Test: cria demanda sem responsável em rotina com autoassignee → atribui; membership revogada → 400; empresa divergente → 400; idempotência preservada.

### Task 12: DEM-006 — remover wrappers mortos de Demandas

**Files:**
- Modify: `DemandasCommands.cs:75-76` (remover `PayloadRef` público — zero consumidores; o privado em `DemandasService.cs:2189` fica), `DemandasService.cs:1842-1846` (remover `UsersWithoutDuplicates` sem chamadas), `DemandRules.cs:254-258` (remover wrappers `EncodeBoardCursor`/`TryDecodeBoardCursor`)
- Test: `DemandRulesTests.cs:299-320` passa a testar `BoardCursor` diretamente (round-trip base64url + rejeição de cursor malformado preservados).

### Task 13: ATT-001 — `status=cancelado` no filtro

**Files:**
- Modify: `src/Modules/Vinx.Erp.Atendimento/AtendimentoCardService.cs:49-56` — adicionar `CancelledColumn` a `ValidColumns`.
- Test: criar → cancelar → `GET /atendimento/cards?status=cancelado` retorna só o cancelado; `overdue=true` continua excluindo terminal; cancelamento via `move` continua negado.

### Task 14: ATT-002 — no-op de watcher valida ExpectedVersion

**Files:**
- Modify: `AtendimentoCardService.cs:789-793` e `:878-882` — nos dois ramos NoChange, comparar `command.ExpectedVersion` com `before.RowVersion`; divergente → caminho de conflito existente (409 integrity_conflict). Versão corrente mantém 200 NoChange sem bump/evento/notificação.
- Test: add repetido com versão antiga → 409; remove ausente com versão antiga → 409; versão corrente → 200 sem efeito colateral (estender `AtendimentoTicketingEndpointTests.cs:352`).

### Task 15: WORK-001 — redator de DLQ preserva tipo operacional

**Files:**
- Modify: `src/Hosts/Vinx.Erp.WorkerHost/WorkerDeadLetterRedactionInterceptor.cs` — adicionar casos explícitos: `InboundAgentRequestReceived` (zera `InboundMessageId`, preserva `Channel`), `DemandasAutomationTick`, `OwnerInvitationDeliveryRequested`/`MemberInvitationDeliveryRequested` (zeram ids). `default:` passa a produzir envelope opaco de redação (novo record `RedactedUnknownWorkerMessage` vazio) em vez de falsificar convite de owner.
- Test: `WorkerHostSecurityContractTests.cs` — `BeforeStoreAsync` diretamente: agente conserva tipo e zera id; desconhecido não vira `OwnerInvitationDeliveryRequested`; exceção/policy estáveis.

### Task 16: TEST-001 — teste de provider ausente real

**Files:**
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/NotificationWorkerSecurityTests.cs:60-90` — renomear teste atual para `Delivery_target_missing_...`; novo modo no `NotificationWorkerHostFactory` que faz só `RemoveAll<INotificationProvider>()`; novo teste: provider ausente → `NotificationProviderMissingException` → DLQ redigida, delivery intacto, sem chamada externa.

### Task 17: PAG-001 — falha de infra inbound → `Unavailable` (503)

**Files:**
- Modify: `src/Modules/Vinx.Erp.Paguai/PaguaiDraftService.cs:289-381` — envolver leitura do candidato em try/catch (padrão `ListAsync:407`), `OperationCanceledException` relançada; resto → log + `PaguaiDraftState.Unavailable` (mapeamento 503 `paguai_unavailable` já existe em `PaguaiController.cs:222-226`).
- Test: revogar `EXECUTE` em `notifications.read_inbound_whatsapp_invoice_candidate` → 503, nenhum draft/receipt/outbox; re-grant ao final (Testcontainer isolado).

### Task 18: CC-002 — `Cache-Control: no-store` para Demandas bearer

**Files:**
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Identity/BrowserBffRequestMiddlewares.cs:20-25` — adicionar `/demandas` à `browserSessionRoute` (espelha `/atendimento`).
- Test: `GET /demandas/board` com bearer → `Cache-Control: private, no-store` (padrão `PaguaiProofEndpointTests.cs:174`).

### Task 19: CC-005 — marcador antiforgery do Paguai unificado no host

**Files:**
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Controllers/PaguaiController.cs` (5 ações) — usar `BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)`; deletar `src/Modules/Vinx.Erp.Paguai/PaguaiAntiforgeryRequiredMetadata.cs`. Contrato OpenAPI preservado (transformer usa `IAntiforgeryRequiredMetadata`).
- Test: `PaguaiDraftEndpointTests.cs:655` (X-CSRF-TOKEN no OpenAPI) + missing-CSRF 400 (:995) + BFF runtime green.

### Task 20: F-14 — deep link `/demandas?demand=<id>` ponta a ponta

**Files:**
- Modify: `src/Foundation/Vinx.Erp.Foundation/Notifications/NotificationInbox.cs:152-173` — `DeepLinks` passa a mapear prefixo `/demandas?demand=` + UUID canônico → `("demandas", selector)`; `/paguai` e `/atendimento` exatos preservados.
- Modify: `apps/web/src/features/notifications/notifications-page.tsx:30,215-217` — allowlist inclui `/demandas` com validação de UUID (espelha `readPaguaiProposalId` em `app-paths.ts:78-87`).
- Tests: projeção backend (inbox item `demanda.*` ganha action) + `notifications-page.test.tsx` renderiza o link; `demandas-page.test.tsx:260` já cobre a UI de chegada.

### Task 21: F-07 — remover `key={mode}` de CompanyAccentScope

**Files:**
- Modify: `apps/web/src/shared/ui/company-accent-scope.tsx:34` — remover `key={mode}` (vars CSS já recomputam por render).
- Test: novo `company-accent-scope.test.tsx` — input com texto sobrevive à troca light/dark; vars/css continuam aplicados.

### Task 22: F-09 — AttachmentField singular substitui arquivo

**Files:**
- Modify: `apps/web/src/shared/ui/attachment-field.tsx:94-115` — com `multiple=false`, nova seleção substitui itens existentes (emite só o novo lote).
- Test: selecionar A → ready → selecionar B → único item, submit envia `fileId` de B; `multiple=true` inalterado (estender `paguai-create-dialog.test.tsx`).

### Task 23: F-10 — EntityDialog ARIA + heading único

**Files:**
- Modify: `apps/web/src/shared/ui/entity-dialog.tsx:34-49` — `aria-labelledby={titleId}`, `DialogTitle` sem `Typography` aninhada (um único `h2` com `id`).
- Test: novo `entity-dialog.test.tsx` — `getByRole("dialog", { name })`, exatamente um heading de título, foco/fechamento preservados.

### Task 24: F-11/F-12 — código morto de frontend

**Files:**
- Delete: `apps/web/src/app/feature-preparation-page.tsx` + `.test.tsx` (zero referências em rotas/manifest/router)
- Modify: `apps/web/src/shared/ui/company-context.tsx:69-71` — remover `useActiveCompany` (zero consumidores; `CompanyProvider` fica)
- Modify: `docs/verification/frontend-workspace-shell-test-cases.md` — linha que cita o teste removido.

### Task 25: F-13 — predicado Company canônico nas páginas

**Files:**
- Modify: `apps/web/src/features/paguai/paguai-page.tsx:163-167`, `demandas/demandas-page.tsx:65-69`, `atendimento/atendimento-page.tsx:124-128,229-231,734-737,746-749` — usar `hasCompanyCapability` de `features/auth/company-capabilities.ts`.
- Test: vitest das 3 páginas green (comportamento inalterado).

### Task 26: F-16 — Zod estrito no demandas-client

**Files:**
- Modify: `apps/web/src/features/demandas/demandas-client.ts:134,148,169-183` — schemas `.strict()` para board/detail/envelope de comando (padrão `paguai-draft-client.ts:21-58`); falha de parse → caminho `demandas_unavailable`/null existente, sem payload bruto.
- Test: `demandas-client.test.ts` — board/detail/command 2xx malformado (campos ausentes, tipos errados, extras) falha seguro; contratos válidos intactos.

### Task 27: SCRIPT-001 — ownership da stack Supabase no verificador

**Files:**
- Modify: `scripts/verify-supabase-storage.sh` — antes do start, `npx supabase status`: stack preexistente → mensagem e exit 2 sem tocar; `cleanup` só executa `supabase stop` se esta execução iniciou a stack.
- Prova: execução com shim fake de `npx` em /tmp (evidência no PR); `DOCKER_CONTEXT`/redaction preservados.

### Task 28: Infra TEST-001 + TEST-002 — projectmem pathspec + launcher no gate

**Files:**
- Modify: `tests/projectmem-pilot.test.mjs:12` — `git ls-files .projectmem` (pathspec).
- Modify: `scripts/verify-public.sh` — novo estágio `node --test scripts/run-real-e2e.test.mjs` após o build (Docker-free standalone; smoke real continua opt-in via env).

### Task 29: FND-09/DOC-001/DOC-002/F-15 — documentação operacional

**Files:**
- Modify: `docs/runbooks/foundation-database.md:41-47` — 13 roles (fonte: `bootstrap-foundation-roles.sql`, login vs NOLOGIN); `docs/modules/foundation.md` — 3 owners faltantes.
- Modify: `README.md:25-34` e `docs/runbooks/local-development.md:17-22` — mapa atual de módulos/rotas (sem reescrever histórico); `local-development.md:55-56` — documentar consumidor atual da rede `vinx-erp` (INFRA-002 via documentação).
- Modify: `docs/runbooks/whatsapp-inbound.md:158-167` — remover do "Corte" o que já está implementado (download/quarentena/scan/OCR/draft).
- Modify: `docs/modules/demandas.md` — deep link passa a documentar o mapeamento fechado (Task 20); `docs/manuals/frontend/app-shell-home.md:5-7` — módulos operacionais atuais.
- Validação: `git diff --check` apenas (sem gate repetido por mudança documental).

### Task 30: Congelamento, gate estratégico, revisão do Sol, PR

- [ ] Rodar os projetos afetados por filtro: Foundation Unit, AI Unit, Demandas Unit; Foundation Integration (filtros novos + afetados); Hosts Integration (Worker/Paguai/BFF); web lint/format/vitest/build/no-direct-supabase; `verify-foundation-database.sh`; `verify-ef-negative-guards.sh`.
- [ ] `./scripts/verify-public.sh` UMA vez (gate estratégico — mudança transversal + segurança).
- [ ] Revisão do Sol do código (read-only) → corrigir findings → re-teste focal.
- [ ] Push + PR com: escopo, decisão por finding (resolvido / fora com motivo), estimativa de diff justificada (>800 linhas por mandato do dono), evidência de gates, status ZC-10 (billing GitHub Actions).
