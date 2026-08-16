# #26 — outbox transacional do convite de dono

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fazer o primeiro produtor durável: a criação de organização persiste
exatamente um envelope opaco para o convite de dono na mesma transação.

**Architecture:** Reutilizar `pending_owner_invitations`, criado pela função
segura de provisionamento. Quando o resultado for `Created`, o ApiHost usa
`IDbContextOutbox` para se inscrever no `FoundationDbContext` e persistir
`OwnerInvitationDeliveryRequested(InvitationId)` dentro do `DbTransaction`
existente. `PublishAsync` persiste o envelope; o serviço mantém
`CommitAsync` como único boundary de negócio e só então tenta
`FlushOutgoingMessagesAsync` como aceleração de despacho. `Replay` não
enfileira novamente. Wolverine PostgreSQL 6.28.2 é somente produtor nesta
fatia. Não haverá handler, worker, provider, endpoint novo, capability nova,
preferência, WhatsApp ou mudança de contrato HTTP.

**Tech Stack:** .NET 10, EF Core/Npgsql existentes, WolverineFx PostgreSQL e
EF Core 6.28.2, PostgreSQL, xUnit/Testcontainers.

## Global Constraints

- Base: `codex/issue-213-outbox-spike`; manter Paguai, Atendimento e Kanban
  completamente fora do diff.
- O evento tem apenas `InvitationId` UUID; correlação fica no envelope. Nunca
  contém nome, e-mail, token, segredo ou payload bruto.
- Manter autorização atual: operador global exato, AAL2, rechecagem SQL, RLS,
  idempotência e a auditoria única `platform.organization.provision`.
- Usar `AutoBuildMessageStorageOnStartup = AutoCreate.None` e
  `OverrideAutoCreateResources(AutoCreate.None)`; startup/runtime/worker não
  criam schema, não têm DDL nem ownership técnico.
- Não usar `AddDbContextWithWolverineIntegration`,
  `MapWolverineEnvelopeStorage`,
  `UseEntityFrameworkCoreWolverineManagedMigrations` ou `AutoProvision`.
- Storage/envelopes técnicos Wolverine ficam fora de `FoundationDbContext`, em
  schemas dedicados `vinx_wolverine` e `vinx_wolverine_queues`. Eles são
  provisionados somente no deploy por identidade migradora com `db-apply`,
  recebem grants mínimos de DML para `vinx_runtime` por script explícito e
  passam por `db-assert` antes da liberação.
- Se a biblioteca não persistir o envelope na transação existente, parar a
  fatia: não criar fila/outbox própria como substituto.
- A rota é única e explicitamente `PublishMessage<OwnerInvitationDeliveryRequested>()`
  para `ToPostgresqlQueue("owner-invitation-delivery").UseDurableOutbox()`.
  Esta é a configuração durável do assinante dessa única saída; não usar a
  policy global `UseDurableOutboxOnAllSendingEndpoints`, fila local, modo
  buffered, handler/listener/provider no ApiHost ou fan-out implícito.
- Falha antes do commit retorna `503` e faz rollback integral. Commit lançado
  ou cancelado é ambíguo: não fazer flush, compensar ou republicar; registrar
  `outbox_commit_outcome_unknown` e deixar o mesmo `requestId` decidir entre
  `Created` e `Replay`. Se o commit retornou e só o flush falhou, manter `201`
  `pending_delivery`, registrar `outbox_flush_deferred` sem PII e deixar o
  relay durável recuperar a mensagem.
- #48 continua dona de listener/worker, lease, retry, DLQ, observabilidade e
  fake de provider; #211B do adapter Supabase/reconciliação; #28 de Meta.

---

### Task 1: Provar a integração transacional antes do runtime

**Files:**
- Test: testes públicos novos ou focados de provisionamento/outbox.
- Test: fixture/factory de integração somente se precisar provisionar storage
  técnico com a identidade migradora.

- [ ] Escrever testes RED que criam organização com operador AAL2 e verificam
  um único envelope `OwnerInvitationDeliveryRequested` opaco quando o resultado
  é `Created`.
- [ ] Escrever testes RED para replay, `409`, AAL1, não-operador e falha da
  persistência do envelope: nenhum envelope extra e rollback integral do grafo
  de organização/convite/recibo/auditoria.
- [ ] Escrever RED para commit ambíguo: a primeira resposta é `503`, não há
  flush e o retry do mesmo `requestId` retorna `Created` ou `Replay` sem
  duplicar envelope/auditoria. Escrever RED separado para falha somente no
  flush pós-commit: retorna `201` `pending_delivery` com um envelope durável.
- [ ] Provar RED de que startup com storage ausente não o cria; o setup ocorre
  apenas por caminho explícito de deploy e `db-assert` detecta storage ausente.

### Task 2: Configurar Wolverine produtor sem ampliar o host

**Files:**
- Modify: projetos e lockfiles necessários de Foundation e ApiHost.
- Create: contrato `OwnerInvitationDeliveryRequested` no módulo Foundation.
- Modify: configuração/DI do ApiHost e
  `PlatformOrganizationProvisioningService`.
- Create: script de deploy para grants mínimos dos schemas técnicos Wolverine.

- [ ] Adicionar os pacotes Wolverine 6.28.2 mínimos e travar os lockfiles.
- [ ] Configurar ApiHost somente como produtor PostgreSQL, usando
  `WOLVERINE_STORAGE_CONNECTION`,
  `PersistMessagesWithPostgresql(...).OverrideAutoCreateResources(AutoCreate.None)`
  e `EnableMessageTransport(...)` para a única fila PostgreSQL explícita e
  durável. Sem handler/WorkerHost/provider. Expor somente os comandos JasperFx
  `db-apply` e `db-assert` para o deploy; a execução normal continua sem setup
  automático.
- [ ] Dentro da transação já aberta de provisionamento, publicar pelo
  `IDbContextOutbox` apenas após `Created`; chamar `Enroll` antes de
  `PublishAsync`, fazer o único `CommitAsync` do serviço e tentar
  `FlushOutgoingMessagesAsync` somente depois de commit confirmado. Nunca usar
  `SaveChangesAndFlushMessagesAsync` nesse fluxo. Preservar cancelamento antes
  do commit; não fazer flush após commit ambíguo; logar somente resultados
  seguros sem PII.
- [ ] Manter `Replay` idempotente, sem segundo envelope ou segunda auditoria.

### Task 3: Documentar operação e fechar a fatia

**Files:**
- Modify: `docs/modules/foundation.md` e runbook de banco/deploy pertinente.
- Create: `docs/verification/transactional-owner-outbox-test-cases.md`.
- Test: testes de migração/privilégio/integração e arquitetura pertinentes.

- [ ] Documentar que `resources setup` roda apenas no deploy com identidade
  migradora, que `db-assert` é pré-requisito de release e que runtime/worker
  não recebem DDL/ownership.
- [ ] Mapear BDD de sucesso, validação, acesso, estado/exceção, concorrência e
  privacidade aos testes; marcar worker/provider/Meta como adiados nas issues
  existentes.
- [ ] Rodar testes públicos focados depois de cada subfatia. Congelar o código,
  pedir revisão independente + revisão Sol de segurança/integridade e rodar
  `./scripts/verify-public.sh` uma única vez antes de publicar a PR draft.
