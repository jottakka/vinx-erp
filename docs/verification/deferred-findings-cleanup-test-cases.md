# Mapa de verificação: limpeza dos findings deferidos (pós-#359)

PR única (mandato do dono) cobrindo os findings deferidos da revisão de
simplicidade com correção segura e prova proporcional no HEAD. Plano completo:
`docs/superpowers/plans/2026-08-28-deferred-findings-cleanup.md`.

| Finding | Mudança | Prova |
| --- | --- | --- |
| FND-02 | `Down` de `AddDocumentCore` mapeia `confirmed`/`verified` → `is_quarantined = false`; backfills (Up e Down) desligam FORCE RLS durante o UPDATE — o migrator (NOBYPASSRLS, sem policy) não via linha alguma e o `SET NOT NULL` falhava | `DocumentCoreMigrationDowngradeTests.Downgrade_keeps_released_files_accessible_and_quarantined_files_isolated` (Testcontainer, down+up reais) |
| FND-03/04/05/11 | Remoção de superfícies públicas sem consumidor: `ListObjectsAsync`, `StorageObjectSummary`, `MalwareScanStatus` do record `StorageObjectInspection`, `FileRecord.IsQuarantined`, `FileRecord.IsPhysicalDeletionAllowed`, `ApprovalCapabilityPolicy.FromCapability`, `CanonicalPayloadHasher.IsValidHash` | Foundation Unit 436 ✓; build da solução; scan/checksum/retenção continuam cobertos por `DocumentVerificationPolicyTests`, `DocumentUploadEndpointTests`, `MetaWhatsAppDocumentInboundTests`, `AuthorizedDocumentSourceTests` ✓ |
| FND-06 | Predicados duplicados nos hosts → delegação única para `MalwareScannerOptions.Validate()`; `ValidateOnStart` e fallback fail-closed preservados | `MalwareScannerContractTests` + composição dos hosts (WorkerHostSecurityContractTests) ✓ |
| FND-07 | `EmployeeDirectoryService` — núcleo comum `ListCoreAsync(requirement)`; `directory.read` ≠ `foundation.employees.create` preservados | EmployeeDirectory endpoints nos testes de integração ✓ |
| FND-08/CC-004 | 28× `new CorrelationContext(actor.CorrelationId).CorrelationId` → `actor.CorrelationId` | Build + suítes de correlação/auditoria existentes ✓ |
| FND-09/DOC-001/DOC-002/F-15 | Inventário de 13 roles, README/runbook local no estado atual, corte do WhatsApp atualizado, shell manual sem módulos fantasma, `git diff --check` | `git diff --check` ✓; roles cruzadas com `bootstrap-foundation-roles.sql` |
| FND-10 | Testes de lifecycle sem reflection; asserts de tautologia removidos | Foundation Unit ✓ |
| AI-02/AI-05 | `AgentTaskProfile.MaxTotalTokens` removido (não persistido, zero leitores); teste reflexivo de `ComposeTurn` removido (cobertura real via `RunAsync` no mesmo arquivo) | AI Unit 359 ✓; linha stale do manifesto `issue-127-132` removida |
| DEM-001 | `CreateRoutineAsync` persiste a rotina antes das etapas (mesma transação ambiente, padrão do próprio `CreateAsync`) → insert order garantido, atomicidade intacta | `Creating_a_routine_persists_stages_and_replays_the_full_response` |
| DEM-002 | Corrida de idempotência: relê o receipt em transação própria com RLS reaplicado → replay com mesmo hash, 409 divergente, nunca 503; auditoria da divergência fora da transação aberta (bug pré-existente de transação aninhada corrigido) | `Concurrent_identical_demand_creates_replay_instead_of_failing_unavailable`, `Divergent_replay_of_the_same_request_returns_conflict` |
| DEM-003 | `ValidateRoutineStages` rejeita `time_elapsed` → `awaiting_approval` (fail-closed, sem ator sintético); entrada manual em aprovação continua aceita | `Creating_a_routine_with_time_elapsed_into_awaiting_approval_is_rejected` |
| DEM-004 | `CreateRoutineAsync`/`UpdateRoutineStatusAsync` retornam as etapas reais | Mesmo teste de rotina + replay com `Stages` idênticos |
| DEM-005 | Responsável explícito vence; sem explícito, rotina roteada resolve `AutoAssigneeMembershipId` (membership ativa mesma empresa revalidada); sem ambos → 400 | `Demand_without_explicit_responsible_resolves_the_routine_auto_assignee` |
| DEM-006 | `PayloadRef` público, `UsersWithoutDuplicates` e wrappers de cursor removidos | Demandas Unit ✓; `DemandRulesTests` testa `BoardCursor` direto |
| ATT-001 | `ValidColumns` aceita `cancelado`; `overdue` continua excluindo terminal | `Status_filter_accepts_the_terminal_cancelado_column_and_overdue_excludes_it` |
| ATT-002 | No-op de watcher (repetição/ausência) valida `ExpectedVersion` → 409; versão corrente segue 200 sem efeito | Extensão de `Watchers_cover_repeat_absent_and_stale_paths_without_side_effects` |
| WORK-001 | Redator de DLQ: casos explícitos Agent/ Tick/Owner/Member (id zerado, canal preservado); desconhecido → `RedactedUnknownWorkerMessage` + exceção opaca `worker_unknown_unexpected` (nada vira convite de owner) | `Dead_letter_redaction_preserves_the_operational_type_and_never_fabricates_owner_invitations` |
| PAG-001 | `CreateFromInboundDocumentAsync` normaliza falha de infraestrutura → `Unavailable` (503 tipado); cancelamento propagado | `Infrastructure_failure_on_the_candidate_read_returns_unavailable_without_partial_state` (REVOKE/GRANT `EXECUTE` de `vinx_runtime`) |
| CC-002 | `/demandas` entra nas rotas de sessão do `BrowserBffNoStoreMiddleware` | `Demandas_responses_carry_no_store_cache_control_for_bearer_clients` |
| CC-005 | `PaguaiController` usa o marcador do host; marcador do módulo deletado; contrato OpenAPI `X-CSRF-TOKEN` preservado | `Draft_create_openapi_declares_json_body_antiforgery_and_safe_error_responses` + BFF CSRF ✓ |
| F-14 | Deep link `/demandas?demand=<uuid>` fechado nos três pontos: parser de prefixo no backend (formato `N` ou hifenizado → href canônico), allowlist da inbox com `isDemandasDeepLink`, leitura canônica em `?demand=` | Testes de `notifications-page` + suítes Demandas ✓ |
| F-07/09/10/11/12/13/16 | `key={mode}` removido; singular substitui arquivo; EntityDialog com `aria-labelledby` e heading único; página morta deletada; `useActiveCompany` removida; `hasCompanyCapability` nas 3 páginas; cliente Demandas com Zod estrito por operação | vitest 628/628 ✓ (novos: `company-accent-scope`, `attachment-field`, `entity-dialog`, malformed-2xx no `demandas-client`) |
| SCRIPT-001 | Verificador Storage recusa stack preexistente e só para stack própria | Shim fake de CLI: stack alheia intocada (exit 2), stack própria parada, start falho sem stop |
| Infra TEST-001 | `git ls-files .projectmem` (pathspec) | `npm run test:projectmem-pilot` ✓ |
| TEST-002 | `node --test scripts/run-real-e2e.test.mjs` entra no gate pós-build (Docker-free) | 48/48 standalone, smoke opt-in ✓ |

Fora da PR (motivo registrado no PR): AI-03 (desfaria feature #135-A), ZC-3/4/5
(DRY de alto risco — fatia própria), ZC-7/ZC-11 (rejeitados), ZC-8 (produto),
ZC-10 (billing do GitHub Actions — só relato).
