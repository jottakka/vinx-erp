# Spike #213 — outbox PostgreSQL Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Registrar uma decisão reproduzível para a primeira infraestrutura durável de outbox PostgreSQL, sem introduzir runtime, worker produtivo ou entrega externa.

**Architecture:** A decisão usa Wolverine com PostgreSQL e integração EF como biblioteca para as futuras #26 e #48. EF é exclusivo do schema VINX/Foundation e das intenções próprias; storage e envelopes técnicos do Wolverine são separados. Esta spike não altera `FoundationDbContext`, migrations, `WorkerHost`, configuração ou lockfiles; ela fixa as guardas para que a adoção futura preserve migrations EF, RLS, auditoria e dados mínimos.

**Tech Stack:** .NET 10, EF Core/Npgsql PostgreSQL, Wolverine PostgreSQL/EF Core, MassTransit apenas como alternativa comparada, Markdown.

## Global Constraints

- Basear a branch em `codex/issue-211-employee-directory`, nunca na UI #233.
- Não instalar dependência, criar migration, tabela, endpoint, job, capability, convite, token, provider ou UI nesta spike.
- Não tocar Paguai, Atendimento ou Kanban.
- Não registrar e-mail, token, segredo, payload externo ou connection string em documentação, evidência ou código.
- A primeira adoção deve manter `AutoCreate.None`. Migrations EF são exclusivas do schema VINX/Foundation e das intenções próprias; Weasel não pode gerir `FoundationDbContext`.
- Storage e envelopes técnicos do Wolverine só podem ser provisionados pelo passo explícito de deploy, nunca por startup ou runtime.
- Timeout externo ambíguo deve resultar em estado observável `provider_ambiguous`, sem retry automático cego; deduplicação interna não promete exactly-once externo.
- A futura outbox guarda somente IDs opacos, tipo de evento e correlação; o worker busca dados pessoais por fronteira estreita autorizada.

---

### Task 1: Registrar a decisão arquitetural

**Files:**
- Create: `docs/adr/0006-outbox-postgresql.md`

- [ ] **Step 1: Registrar a comparação comprovada**

Documentar que o scratch descartável de 2026-08-16 restaurou e compilou, em .NET 10, WolverineFx.Postgresql/WolverineFx.EntityFrameworkCore 6.28.2 e MassTransit.EntityFrameworkCore 9.2.0 sem conflito EF Core/Npgsql 10. Não incluir código, configuração ou caminho temporário do scratch.

- [ ] **Step 2: Fixar a escolha e as guardas**

Escolher Wolverine para PostgreSQL sem broker. Exigir que #26 e #48, conforme a responsabilidade de cada uma, provem `AutoBuildMessageStorageOnStartup = AutoCreate.None`, `OverrideAutoCreateResources(AutoCreate.None)` e que `FoundationDbContext` não use `UseEntityFrameworkCoreWolverineManagedMigrations()`. Exigir que `resources setup` (ou equivalente) execute somente com identidade migradora/deploy, que `db-assert` valide storage Wolverine antes da liberação e que runtime/worker não tenham DDL nem ownership desses schemas. Manter EF exclusivo do schema VINX/Foundation e das intenções próprias; storage/envelopes técnicos Wolverine são separados e só podem ser provisionados por esse deploy explícito. Declarar que MassTransit volta à avaliação apenas se houver broker ou saga que justifique sua topologia/licença. Não afirmar que esta spike executou essas provas.

- [ ] **Step 3: Delimitar as próximas PRs**

Vincular #26 somente à intenção/outbox transacional. Fixar #48 como infraestrutura genérica de worker, lease, retry, DLQ e observabilidade, com prova por fake/contrato de provider e sem chamada real a provider de convite. Fixar #211B, após #48, como única dona do adapter Supabase Admin de convite e da reconciliação real de `provider_ambiguous`; ela usa somente `issuer+subject` confirmado, nunca email. Adapter Meta/WhatsApp e webhook de notificações ficam fora desta spike e dependem da cadeia de notificações. Fixar a proibição de PII/segredo na outbox, DLQ, log e auditoria.

### Task 2: Mapear a verificação da decisão

**Files:**
- Create: `docs/verification/outbox-library-spike-test-cases.md`

- [ ] **Step 1: Mapear as cinco classes BDD da issue #213**

Criar uma tabela curta para sucesso, validação/erros, acesso, estado/exceções e concorrência. Marcar como evidência da spike somente o restore/build do scratch e a revisão dos contratos oficiais; apontar explicitamente as provas de transação para #26, infraestrutura genérica de worker/lease/retry/DLQ/observabilidade para #48 e adapter/reconciliação real de convite para #211B, sem afirmar que já existem.

- [ ] **Step 2: Registrar critérios de adoção futura**

Exigir que #26 prove rollback sem intenção/outbox e commit atômico. Exigir que #48 prove dois workers sem dupla conclusão interna, retry/DLQ observáveis, logs sem payload sensível e contrato fake de provider, sem chamada externa real. Exigir que #211B prove adapter Supabase Admin de convite e reconciliação real de `provider_ambiguous` por `issuer+subject` confirmado.

### Task 3: Validar e publicar a documentação

**Files:**
- Modify: `docs/adr/0006-outbox-postgresql.md`
- Modify: `docs/verification/outbox-library-spike-test-cases.md`

- [ ] **Step 1: Verificar coerência e escopo**

Executar `rg -n -i "connection string|apikey|service.role|token|e-mail" docs/adr/0006-outbox-postgresql.md docs/verification/outbox-library-spike-test-cases.md` e revisar cada ocorrência; o resultado esperado é ausência de credencial, payload ou dado pessoal.

- [ ] **Step 2: Executar validações proporcionais**

Executar `git diff --check`, validar que o diff contém somente os três documentos desta spike e revisar os links oficiais. Não executar migration, worker, chamada de provider ou gate completo porque a fatia é documental.

- [ ] **Step 3: Commit e revisão independente**

Fazer commit `docs(adr): decide outbox PostgreSQL`, pedir uma revisão independente de escopo e de segurança da decisão, depois publicar uma PR draft empilhada sobre `codex/issue-211-employee-directory`.
