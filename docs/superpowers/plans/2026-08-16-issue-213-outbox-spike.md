# Spike #213 — outbox PostgreSQL Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Registrar uma decisão reproduzível para a primeira infraestrutura durável de outbox PostgreSQL, sem introduzir runtime, worker produtivo ou entrega externa.

**Architecture:** A decisão usa Wolverine com PostgreSQL e integração EF somente como a biblioteca escolhida para as futuras #26 e #48. Esta spike não altera `FoundationDbContext`, migrations, `WorkerHost`, configuração ou lockfiles; ela fixa as guardas para que a adoção futura preserve migrations EF, RLS, auditoria e dados mínimos.

**Tech Stack:** .NET 10, EF Core/Npgsql PostgreSQL, Wolverine PostgreSQL/EF Core, MassTransit apenas como alternativa comparada, Markdown.

## Global Constraints

- Basear a branch em `codex/issue-211-employee-directory`, nunca na UI #233.
- Não instalar dependência, criar migration, tabela, endpoint, job, capability, convite, token, provider ou UI nesta spike.
- Não tocar Paguai, Atendimento ou Kanban.
- Não registrar e-mail, token, segredo, payload externo ou connection string em documentação, evidência ou código.
- A primeira adoção deve manter `AutoCreate.None`, deixar migrations exclusivamente no EF e impedir Weasel de gerir `FoundationDbContext`.
- Timeout externo ambíguo deve resultar em estado observável `provider_ambiguous`, sem retry automático cego; deduplicação interna não promete exactly-once externo.
- A futura outbox guarda somente IDs opacos, tipo de evento e correlação; o worker busca dados pessoais por fronteira estreita autorizada.

---

### Task 1: Registrar a decisão arquitetural

**Files:**
- Create: `docs/adr/0006-outbox-postgresql.md`

- [ ] **Step 1: Registrar a comparação comprovada**

Documentar que o scratch descartável de 2026-08-16 restaurou e compilou, em .NET 10, WolverineFx.Postgresql/WolverineFx.EntityFrameworkCore 6.28.2 e MassTransit.EntityFrameworkCore 9.2.0 sem conflito EF Core/Npgsql 10. Não incluir código, configuração ou caminho temporário do scratch.

- [ ] **Step 2: Fixar a escolha e as guardas**

Escolher Wolverine para PostgreSQL sem broker e registrar `AutoBuildMessageStorageOnStartup = AutoCreate.None` e `OverrideAutoCreateResources(AutoCreate.None)` como requisitos da adoção futura. Declarar que `UseEntityFrameworkCoreWolverineManagedMigrations()` não pode gerir `FoundationDbContext` e que MassTransit volta à avaliação apenas se houver broker ou saga que justifique sua topologia/licença.

- [ ] **Step 3: Delimitar as próximas PRs**

Vincular #26 à intenção/outbox transacional e #48 ao worker/retry/DLQ; deixar adapter Supabase/Meta e aceite de convite fora. Fixar a política de timeout ambíguo e a proibição de PII/segredo na outbox, DLQ, log e auditoria.

### Task 2: Mapear a verificação da decisão

**Files:**
- Create: `docs/verification/outbox-library-spike-test-cases.md`

- [ ] **Step 1: Mapear as cinco classes BDD da issue #213**

Criar uma tabela curta para sucesso, validação/erros, acesso, estado/exceções e concorrência. Marcar como evidência da spike somente o restore/build do scratch e a revisão dos contratos oficiais; apontar explicitamente as provas de transação, lease, retry, DLQ e provider para #26/#48, sem afirmar que já existem.

- [ ] **Step 2: Registrar critérios de adoção futura**

Exigir que #26 prove rollback sem intenção/outbox e commit atômico, e que #48 prove dois workers sem dupla conclusão interna, retry/DLQ observáveis e logs sem payload sensível.

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
