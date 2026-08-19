# [Notifications] Worker, retry e DLQ (#48 — fatia W2) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) ou superpowers:executing-plans. Steps com checkbox (`- [ ]`).

**Goal:** consumir a fila durável `notification-delivery` com claim único (lease por token de tentativa), provider chamado fora de transação, retry finito (1s/5s/30s), DLQ redigida, estados monotônicos e métricas — sem regra Meta no worker.

**Architecture:** espelha o pipeline do convite de dono (#211B): ingresso idempotente → claim `FOR UPDATE` com `provider_attempt_id` → chamada ao `INotificationProvider` sem transação aberta → finalize por token. Funções `SECURITY DEFINER` no schema `notifications` (owner `vinx_notifications_owner`, EXECUTE só para `vinx_worker`); retry fica com a failure policy do Wolverine (dono único do retry); `provider_ambiguous` é terminal sem retry cego, aguardando reconciliação do W3. A fatia W1 (#243, ainda não mergeada) é a base: PR empilhado, re-alvejado ao main após o merge dela.

**Tech Stack:** idem W1 (.NET 10, Wolverine 6.28.2, EF/Npgsql, Testcontainers, xUnit) + System.Diagnostics.Metrics.

## Global Constraints

- Base: branch `codex/issue-26-notification-outbox` (head W1) → branch `codex/issue-48-notification-worker`; PR com base no branch W1 (re-alvejar ao main após merge do #243).
- Não modificar handler/fila/segurança do convite de dono; única exceção compartilhada: `WorkerDeadLetterRedactionInterceptor` ganha um branch por tipo de mensagem mantendo o path do convite idêntico bit a bit.
- Provider jamais chamado dentro de transação aberta; sem chamada a provider em request HTTP; sem interpretar mensagem; sem webhook Meta; sem DDL em startup (`AutoCreate.None` intocado).
- Envelope e DLQ sem PII/headers/payload original; exceção com tipo estável (`invalid`|`retry_exhausted`|`unexpected`|`provider_missing`).
- Status monotônico: `pending_delivery → pending_provider → provider_in_flight → delivered|provider_failed|provider_ambiguous`; estados terminais imutáveis; replay não regredige nem duplica; sem confirmação falsa.
- `NotificationProviderTransientException` (infra/5xx/rate-limit no W3) propaga para ScheduleRetry(1s,5s,30s) e esgotamento → DLQ `retry_exhausted`; timeout do provider = resultado `Ambiguous` (terminal), NÃO retry.
- Métricas `Meter("Vinx.Erp.Notifications.Delivery")`: histograma duração + contadores outcome/DLQ, labels de baixa cardinalidade (`outcome`, `channel`, `reason`); sem delivery id/destinatário.
- Logs só em falha/transição, template `{Operation} {Module} {Result} {Reason}`, sem destinatário/telefone.
- Estimativa: ~21 arquivos, ~1.400-1.700 linhas manuais — acima do alvo de 800 pela coesão (uma só responsabilidade: consumir a entrega), justificativa registrada aqui e no PR, conforme AGENTS.md.
- Gate `./scripts/verify-public.sh` EXIT:0 no HEAD exato, serializado.

## Padrões canônicos (espelhar, na ordem)

1. Handler + failure policy + DLQ: `src/Hosts/Vinx.Erp.WorkerHost/OwnerInvitationDeliveryRequestedHandler.cs` (validação de envelope, `CustomActionIndefinitely`, `MoveToDeadLetterStorageAsync` após redação, `lifecycle.CompleteAsync`).
2. Orquestração claim/finalize: `src/Foundation/Vinx.Erp.Foundation/Platform/OwnerInvitationProviderDeliveryService.cs` (ingress → final → replay; claim `FOR UPDATE`; provider fora da tx; finalize por token; `PostgresException` check-violation → permanente).
3. SQL das funções: migração `20260817001000_AddOwnerInvitationProviderDelivery.cs` (SELECT FOR UPDATE, `gen_random_uuid` de attempt, verificação de token no finalize, auditoria na transição, REVOKE PUBLIC/migrator + GRANT `vinx_worker`).
4. Hosting de worker em teste: `WorkerHostIngressTests.cs` (`extern alias workerhost`, `WorkerHostFactory` com `FOUNDATION_WORKER_CONNECTION` + `ConfigureTestServices`).
5. Contratos W1 nesta base: `src/Foundation/Vinx.Erp.Foundation/Notifications/*`, migração `20260818120000`, helper de teste `NotificationIntentSeed.cs`.

---

### Task 1: Migração `20260819120000_AddNotificationDeliveryWorker`

**Files:** Create `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260819120000_AddNotificationDeliveryWorker.cs` (raw SQL, `[DbContext]`/`[Migration]` inline, sem Designer).

**Interfaces produzidas (Tasks 2-3 consomem):**
- `notifications.accept_notification_delivery(p_delivery_id uuid, p_correlation_id uuid) RETURNS TABLE(status text, transitioned boolean)` — `pending_delivery→pending_provider`; idempotente; `not_found` para id inexistente; audita `notification.delivery.ingress` (ator = `recipient_user_id`, source `worker`).
- `notifications.claim_notification_delivery(p_delivery_id uuid) RETURNS TABLE(status text, attempt_id uuid, channel text, claimed boolean)` — `FOR UPDATE`; `pending_provider→provider_in_flight`; minta `provider_attempt_id`, `attempt_count+1`, `provider_claimed_at`; retorna canal; se já `provider_in_flight`/terminal → `claimed=false` com status atual.
- `notifications.finalize_notification_delivery(p_delivery_id uuid, p_attempt_id uuid, p_correlation_id uuid, p_outcome text, p_failure_reason text) RETURNS TABLE(status text, finalized boolean)` — `p_outcome ∈ ('delivered','failed','ambiguous')`; somente `provider_in_flight` com token igual; terminal → `finalized=false` com status atual; audita `notification.delivery.provider`.

- [ ] **Step 1: SQL da migração** (estrutura):

```sql
-- Up (como vinx_migrator):
ALTER TABLE notifications.notification_intents DROP CONSTRAINT ck_notification_intents_status; -- nome real: o CHECK inline da W1 não tem nome; usar ALTER TABLE ... DROP CONSTRAINT <nome gerado>.
-- Na prática: recriar por nome: primeiro `ALTER TABLE ... DROP CONSTRAINT IF EXISTS notification_intents_status_check;` (nome padrão do PG para CHECK inline de tabela criada por DDL é notification_intents_status_check).
ALTER TABLE notifications.notification_intents
    ADD CONSTRAINT notification_intents_status_check CHECK (status IN (
        'pending_delivery','pending_provider','provider_in_flight',
        'delivered','provider_failed','provider_ambiguous')),
    ADD COLUMN provider_attempt_id uuid NULL,
    ADD COLUMN provider_claimed_at timestamp with time zone NULL,
    ADD COLUMN provider_resolved_at timestamp with time zone NULL,
    ADD COLUMN failure_reason text NULL
        CHECK (failure_reason IS NULL OR failure_reason IN ('provider_rejected','provider_ambiguous'));
ALTER TABLE notifications.notification_intents
    ADD CONSTRAINT notification_intents_lifecycle_check CHECK (
        (status = 'pending_delivery' AND provider_attempt_id IS NULL AND provider_claimed_at IS NULL AND provider_resolved_at IS NULL)
        OR (status = 'pending_provider' AND provider_attempt_id IS NULL AND provider_claimed_at IS NULL AND provider_resolved_at IS NULL)
        OR (status = 'provider_in_flight' AND provider_attempt_id IS NOT NULL AND provider_claimed_at IS NOT NULL AND provider_resolved_at IS NULL)
        OR (status = 'delivered' AND provider_attempt_id IS NOT NULL AND provider_resolved_at IS NOT NULL AND failure_reason IS NULL)
        OR (status IN ('provider_failed','provider_ambiguous') AND provider_attempt_id IS NOT NULL AND provider_resolved_at IS NOT NULL AND failure_reason IS NOT NULL));

SET ROLE vinx_notifications_owner;
-- accept_notification_delivery: UPDATE condicional idempotente (padrão accept_owner_invitation_delivery):
--   SELECT ... FOR UPDATE; status='pending_delivery' → UPDATE para 'pending_provider' + audit(transição);
--   status>=pending_provider → (status, false); id inexistente → ('not_found', false).
-- claim_notification_delivery: SELECT id, channel, status FOR UPDATE;
--   status='pending_provider' → v_attempt := gen_random_uuid(); UPDATE provider_in_flight, attempt_count = attempt_count+1,
--   provider_attempt_id = v_attempt, provider_claimed_at = clock_timestamp(); RETURN ('provider_in_flight', v_attempt, channel, true);
--   senão RETURN (status atual, provider_attempt_id existente, channel, false).
-- finalize_notification_delivery: SELECT ... FOR UPDATE;
--   p_outcome mapa: delivered→'delivered'; failed→'provider_failed'+'provider_rejected'; ambiguous→'provider_ambiguous'+'provider_ambiguous';
--   valida p_outcome (22023 em parâmetro inválido); status='provider_in_flight' AND provider_attempt_id = p_attempt_id →
--   UPDATE status/provider_resolved_at/failure_reason + audit notification.delivery.provider (outcome, reason);
--   terminal → (status, false); token divergente → (status, false); not_found → ('not_found', false).
REVOKE ALL ON FUNCTION ... FROM PUBLIC, vinx_migrator; GRANT EXECUTE ... TO vinx_worker; (×3)
RESET ROLE;

-- Down: guard (existe linha com status <> 'pending_delivery' → 23514), DROP FUNCTION ×3, DROP CONSTRAINT lifecycle+status check recriando o original (status IN ('pending_delivery')), DROP COLUMN ×4.
```

Transcrever com SQL completo (sem `...`) seguindo literalmente a estrutura de `20260817001000` (mensagens de exceção estáveis, `pg_advisory_xact_lock` NÃO necessário — dedup já é por chave da fila + token).

- [ ] **Step 2:** `dotnet build src/Foundation/Vinx.Erp.Foundation/Vinx.Erp.Foundation.csproj` → 0 errors.
- [ ] **Step 3:** Commit `feat(notifications): add delivery worker state machine with claim and finalize functions`.

### Task 2: Módulo `Vinx.Erp.Notifications` (provider boundary + worker service + métricas)

**Files (Create):**
- `src/Modules/Vinx.Erp.Notifications/Vinx.Erp.Notifications.csproj` (net10.0, ref Foundation; sem FrameworkReference — classlib puro)
- `Notifications/INotificationProvider.cs` — `string Channel { get; }` + `Task<NotificationProviderResult> DeliverAsync(Guid deliveryId, CancellationToken ct)`
- `Notifications/NotificationProviderResult.cs` — `enum NotificationProviderResultKind { Delivered, Rejected, Ambiguous }` + record com `FailureReason?`
- `Notifications/NotificationProviderTransientException.cs` — infra transitiva retryável
- `Notifications/NotificationProviderMissingException.cs` — canal sem provider registrado (config)
- `Notifications/InAppNotificationProvider.cs` — `Channel = "in_app"`, retorna `Delivered` sem efeito externo (o alerta in-app É a linha de intenção; leitura de inbox é #56)
- `Notifications/NotificationProviderResolver.cs` — `IEnumerable<INotificationProvider>` → `Resolve(channel)` lança `NotificationProviderMissingException` se ausente
- `Notifications/NotificationDeliveryWorkerService.cs` — orquestração (accept → final? replay : claim → claimed? provider fora de tx : replay → finalize por kind); `NotificationDeliveryResult(Status, Finalized)`; abre transação curta só para accept/claim/finalize (ADO bruto no padrão do `OwnerInvitationProviderDeliveryService`); emite métricas
- `Notifications/NotificationDeliveryMetrics.cs` — Meter singleton com `RecordOutcome(channel, outcome)`, `RecordDuration(channel, ms)`, `RecordDeadLetter(reason)`; exportável via listener

- [ ] Steps: escrever arquivos completos → build → commit `feat(notifications): add provider boundary, in-app provider and delivery worker service`.

### Task 3: WorkerHost (handler + policy + DLQ + wiring)

**Files:**
- Create `src/Hosts/Vinx.Erp.WorkerHost/NotificationDeliveryRequestedHandler.cs` — valida `DeliveryId`/correlation → `NotificationDeliveryDeadLetterException(Invalid)`; `NotificationProviderMissingException` → dead letter `provider_missing`; delega ao service; log de transição.
- Create `src/Hosts/Vinx.Erp.WorkerHost/NotificationDeliveryFailurePolicy.cs` — `ModifyHandlerChainAttribute`: `OnException<NotificationDeliveryDeadLetterException>` → DLQ invalid; `OnException<NotificationProviderTransientException>` → ScheduleRetry(1s,5s,30s).Then DLQ retry_exhausted; `OnException<NpgsqlException>(IsTransient)` → idem; `OnAnyException` → DLQ unexpected. Persistência espelha `PersistRedactedWorkerDeadLetterAsync` (redação + `MoveToDeadLetterStorageAsync` + `CompleteAsync`).
- Create `src/Hosts/Vinx.Erp.WorkerHost/NotificationDeliveryDeadLetterException.cs` — constantes `InvalidExceptionType`/`RetryExhaustedExceptionType`/`UnexpectedExceptionType`/`ProviderMissingExceptionType`.
- Modify `src/Hosts/Vinx.Erp.WorkerHost/WorkerDeadLetterRedactionInterceptor.cs` — `RedactEnvelope` faz switch por tipo (`OwnerInvitationDeliveryRequested` → path atual idêntico; `NotificationDeliveryRequested` → `new NotificationDeliveryRequested(Guid.Empty)`); `BeforeStoreAsync` retorna exceção do tipo correspondente. Path do convite inalterado.
- Modify `src/Hosts/Vinx.Erp.WorkerHost/Program.cs` — `Discovery.IncludeType<NotificationDeliveryRequestedHandler>()`; `ListenToPostgresqlQueue("notification-delivery").UseDurableInbox()`; `AddScoped` dos serviços do módulo (`NotificationDeliveryWorkerService`, `NotificationProviderResolver`, `InAppNotificationProvider` como `INotificationProvider`, `NotificationDeliveryMetrics` singleton); usings.
- Modify `src/Hosts/Vinx.Erp.WorkerHost/Vinx.Erp.WorkerHost.csproj` (ref módulo) + `Vinx.Erp.sln` (projeto novo).

- [ ] Steps: escrever → `dotnet build Vinx.Erp.sln` 0 errors → commit `feat(notifications): consume notification-delivery queue with lease, retry and sanitized DLQ`.

### Task 4: Testes (RED → GREEN)

**Files:**
- Create `tests/public/Vinx.Erp.Foundation.IntegrationTests/NotificationWorkerHostFactory.cs` — `WebApplicationFactory<workerhost::Program>` com `FOUNDATION_WORKER_CONNECTION`, env Supabase dummy e `ConfigureTestServices` substituindo `INotificationProvider` por fakes (`FakeNotificationProvider` configurável: contagem de chamadas, resultado, throw transitivo).
- Create `tests/public/Vinx.Erp.Foundation.IntegrationTests/NotificationWorkerTests.cs` — fluxo completo: enqueue via `FoundationApiFactory` + `NotificationIntentSeed` (W1) → sobe worker no MESMO fixture (`factory.Fixture.WorkerConnectionString`) → poll do status:
  1. **Sucesso in-app:** worker conclui `delivered`, uma auditoria `notification.delivery.provider`, métricas outcome `delivered` capturadas por `MeterListener`.
  2. **Sucesso whatsapp (fake):** opt-in + fake provider Delivered → `delivered`, uma chamada.
  3. **Ambíguo:** fake lança timeout (resultado `Ambiguous`) → `provider_ambiguous`, exatamente UMA chamada (sem retry cego), mensagem consumida.
  4. **Rejeitado:** fake `Rejected` → `provider_failed` com `failure_reason='provider_rejected'`.
  5. **Estado/replay:** segunda execução do worker sobre entrega final → `finalized=false`, status imutável, sem nova chamada; entrega em `provider_in_flight` por outra instância → `claimed=false`, sem chamada.
  6. **Concorrência:** dois workers (duas factories no mesmo fixture) + uma entrega whatsapp → exatamente uma chamada ao provider e um `delivered`.
  7. **Transitivo:** fake lança `NotificationProviderTransientException` → row NÃO termina `delivered` após esgotar retries (permanece `pending_provider`/`in_flight`) — sem confirmação falsa.
- Create `tests/public/Vinx.Erp.Foundation.IntegrationTests/NotificationWorkerSecurityTests.cs` —
  8. **Envelope inválido:** `Handle` direto com `DeliveryId` vazio/correlação ausente → `NotificationDeliveryDeadLetterException` (invalid), sem chamada ao provider.
  9. **Provider ausente:** canal sem provider → dead letter `provider_missing`, sem chamada externa.
  10. **DLQ redigida:** `RedactEnvelope` em envelope notification → corpo `NotificationDeliveryRequested(Guid.Empty)`, headers vazios; envelope do convite continua idêntico ao path atual.
  11. **Falha fechada:** revoke EXECUTE das três funções de `vinx_worker` → worker não entrega (`pending_delivery` persiste), sem chamada ao provider, sem crash do host.
  12. **Privilégios:** `has_function_privilege('vinx_worker', funções, 'EXECUTE')` true ×3; `vinx_runtime` false ×3; runtime/worker continuam sem DML direto nas tabelas.

- [ ] Steps: escrever fakes+testes → `dotnet test --filter "FullyQualifiedName~NotificationWorker"` → RED → corrigir fonte (nunca afrouxar teste) → GREEN → regressões `~NotificationIntent` + `~WorkerHostIngress` + `~PlatformOrganizationProvisioning` → commit `test(notifications): cover delivery worker success, ambiguity, concurrency and dead-letter redaction`.

### Task 5: Docs

- `docs/modules/notifications.md` (novo, headings do `_template`: Ownership/Company scope/Contract version/Deletion strategy/Migration notes).
- `docs/verification/issue-48-notification-worker-test-cases.md` (tabela BDD × teste × situação).
- `docs/modules/foundation.md`: estender a seção de notificações com o lado worker (fila consumida, estados, DLQ, métricas).
- Commit `docs(notifications): document delivery worker states, DLQ and verification cases`.

### Task 6: Gate + PR empilhado

- `./scripts/verify-public.sh` EXIT:0 no HEAD exato (serializado; primeira falha → corrigir → re-gate integral).
- `git push -u origin codex/issue-48-notification-worker`; `gh pr create --draft --base codex/issue-26-notification-outbox` (empilhado no #243; re-alvejar ao main quando o W1 mergear).
- Evidência no PR (base/head, gate, suítes) + comentário na issue #48.

## Self-review

- Handoff W2 × plano: lease concorrente ✓ (T4.5/6), sucesso ✓ (1/2), timeout transitório → dois caminhos distintos e corretos: timeout do PROVIDER = Ambiguous terminal (3), infra transitiva = ScheduleRetry finito + DLQ retry_exhausted (7); erro definitivo → provider_failed sem retry (4); cancelamento → coberto pelo OnAnyException→DLQ unexpected (sem confirmação falsa); retry esgotado ✓ (7 + DLQ); envelope inválido ✓ (8); provider ambíguo ✓ (3, uma chamada só); claim único com fronteira estreita ✓ (função retorna só status/token/canal); provider fora de transação ✓ (service só abre tx curtas em accept/claim/finalize); DLQ redigida com tipo estável ✓ (9/10 + policy); monotônico ✓ (CHECK + finalize por token + testes 5); métricas low-cardinality ✓; logs só transição/falha ✓; sem Meta/webhook/inbound ✓; sem DDL startup ✓ (guards existentes + teste 12); worker sem grant falha fechado ✓ (11).
- Tipos consistentes entre tasks: `NotificationDeliveryRequested(Guid DeliveryId)` (W1) consumido pelo handler; service retorna `(Status, Finalized)`; funções SQL como declaradas na Task 1.
- 21 arquivos < 25; linhas > 800 com justificativa registrada.
