# Foundation database migrations and snapshot

Foundation EF migrations are the only business-schema creation path. The
committed schema snapshot is a reviewed `pg_dump` artifact generated only after
those migrations run; it is never an input to schema creation.

Run the normal public check from the repository root:

```bash
./scripts/verify-foundation-database.sh
```

It creates and removes its own `postgres:17.9-alpine` container, runs the
public role bootstrap, checks the current Foundation model for pending changes,
applies the migration history and compares a normalized `foundation` and
`audit` schema dump with `docs/database/foundation-schema.snapshot.sql`.
The design-time EF commands receive only an ephemeral migrator connection; the
verifier neither starts the API host nor creates a runtime credential.

After a reviewed migration change, regenerate the committed artifact only with
the explicit write switch:

```bash
./scripts/generate-foundation-schema-snapshot.sh --write
```

Without `--write`, that generator creates only a temporary snapshot and leaves
the committed artifact unchanged. The EF negative guard is separate from the
current-tree check: its controlled drift project proves the model-pending
command fails for a real difference, while its failing `BrokenMigration` proves
that EF does not record a failed migration in `__EFMigrationsHistory`.

## Legacy reference preflight

Task 7 used a disposable detached clone of
`https://github.com/cbqoliveira/vinx-erp.git` at resolved HEAD
`2ccb0b40fa38925b8aa1bd32a38ade12d7edab47`. The clone was read-only: no
legacy migration, service, DDL or data command was run. Discovery was limited
to `rg` inside that explicit clone.

The fixed current Foundation database roles (fonte canônica:
`infra/database/bootstrap-foundation-roles.sql`; nenhuma migration cria role):

Login (aplicação e migração):

- `vinx_migrator LOGIN NOINHERIT NOBYPASSRLS`
- `vinx_runtime LOGIN NOINHERIT NOBYPASSRLS`
- `vinx_worker LOGIN NOINHERIT NOBYPASSRLS`

`NOLOGIN` (ownership por schema/módulo):

- `vinx_audit_owner NOLOGIN NOINHERIT NOBYPASSRLS`
- `vinx_identity_owner NOLOGIN NOINHERIT NOBYPASSRLS`
- `vinx_authorization_owner NOLOGIN NOINHERIT NOBYPASSRLS`
- `vinx_platform_owner NOLOGIN NOINHERIT NOBYPASSRLS`
- `vinx_provisioning_owner NOLOGIN NOINHERIT NOBYPASSRLS`
- `vinx_employee_directory_owner NOLOGIN NOINHERIT NOBYPASSRLS`
- `vinx_demandas_owner NOLOGIN NOINHERIT NOBYPASSRLS`
- `vinx_notifications_owner NOLOGIN NOINHERIT NOBYPASSRLS`
- `vinx_permissions_owner NOLOGIN NOINHERIT NOBYPASSRLS`
- `vinx_worker_owner NOLOGIN NOINHERIT NOBYPASSRLS`

`vinx_identity_owner` possui somente `USAGE` em `foundation`, `SELECT` em
`foundation.users` e `UPDATE` somente em `sessions_valid_after`, enquanto
existir a função de admissão de sessão. O último é o privilégio mínimo para o
`FOR UPDATE` da função `SECURITY DEFINER`; `vinx_runtime` não o recebe e não
tem leitura direta de `users`. `vinx_migrator` pode assumir o owner apenas
durante DDL.

## Provisionamento inicial de organização

A migration de provisionamento cria as tabelas técnicas
`foundation.pending_owner_invitations` e
`foundation.organization_provisioning_receipts` com RLS forçado. Elas são
propriedade de `vinx_provisioning_owner`; o runtime não recebe privilégio de
tabela. A execução disponível ao runtime é somente
`foundation.provision_platform_organization(...)`, `SECURITY DEFINER`, com
`search_path` fixo e `pg_temp` por último. Antes de gerar o snapshot, confirme
que o teste de função cobre criação, replay, conflito, operador ausente e AAL1
sem mutação. O `Down` só remove as tabelas quando não há estado observável e
restaura as policies de tabelas existentes como migrador, após sair da role
dedicada.

These current-repository roles are distinct from the legacy Supabase roles
`anon`, `authenticated` and `service_role`, which are reference evidence only.
Any future isolated database check uses `postgres:17.9-alpine` and a
schema-only snapshot boundary:

```bash
pg_dump --schema-only --no-owner --no-privileges \
  --schema=foundation --schema=audit
```

The snapshot must contain no data and must not be used to create the schema.
Production preflight, provider state and deployment remain outside this task
and belong to issue #70.
