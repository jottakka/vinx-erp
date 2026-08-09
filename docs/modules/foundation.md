# Foundation module contract

Foundation owns the cross-cutting contracts that every ERP module consumes. It does not own Paguai, Service Desk, Kanban, HR, Notifications or AI business rules.

## Ownership

- Groups, companies, units, teams, users, employees and active memberships.
- Roles, named capabilities and scope grants: `self`, `team`, `unit`, `company` and `group`.
- Configurable approval policies and alçadas. A sole owner approver is data configuration, never a hard-coded module rule.
- Identity and file-storage provider boundaries: `IdentityProvider` and `FileStorage`.
- Durable audit, correlation context, redaction, OpenTelemetry conventions, health checks, idempotency keys and outbox technical contracts.

## Company scope

All commands carry one company. A person can hold different memberships, roles
and scopes in more than one company. The active context is one company or a
read-only all-authorized view. Every command, including an MCP command,
provides one explicit `empresa_id`; the server validates membership, capability
and scope regardless of frontend visibility.

The frontend receives effective capabilities only to shape menus, routes,
buttons, columns and data. It is never the authorization authority. API, MCP
and database protections deny forged or cross-company requests.

## Identidade autenticada

A API valida o JWT do Supabase Auth e resolve o ator interno exclusivamente
pelo par imutável `issuer` + `subject` em `foundation.external_identities`.
E-mail não participa da resolução e uma identidade externa desconhecida recebe
`401`; a API não cria `User` nem `ExternalIdentity` durante uma requisição.
O runtime possui somente `SELECT` nessa tabela para essa consulta.
Configure a API com `SUPABASE_AUTH_ISSUER` e `SUPABASE_AUTH_AUDIENCE`;
`FOUNDATION_RUNTIME_CONNECTION`, `SUPABASE_STORAGE_URL` e
`SUPABASE_STORAGE_SERVICE_ROLE_KEY` continuam obrigatórias para a infraestrutura.

## Contract version

`1.0`

## Modelo de entidades

Entidades mutáveis compartilham identidade, auditoria, exclusão lógica e versão de concorrência por classes-base do domínio, sem dependência do EF. O mapeamento permanece em `Persistence`, e somente entidades que pertencem a uma empresa implementam `ICompanyScoped`.

## Data and audit rules

Business and configuration data use soft delete with `deleted_at`, `deleted_by_user_id` and `deleted_reason`; a restore is authorized and audited. Audit events are append-only and use retention rather than soft delete. Module data remains explicitly company-scoped; an authorized group-wide view is a union, never a tenant-less record.

Idempotent writes replay a matching request and payload. Reusing a request with a
different payload returns `idempotency_conflict`; a separate write that loses a
business-database integrity race returns the redacted `integrity_conflict` and
records a denied audit event. Neither response exposes database diagnostics.

## Deletion strategy

See Data and audit rules above; this section introduces no second deletion policy.

## Migration notes

The first migration is `InitialFoundationBaseline`.

The current database ownership boundary uses `vinx_migrator LOGIN NOINHERIT
NOBYPASSRLS`, `vinx_runtime LOGIN NOINHERIT NOBYPASSRLS` and
`vinx_audit_owner NOLOGIN NOINHERIT NOBYPASSRLS`. These roles are distinct from
the legacy Supabase `anon`, `authenticated` and `service_role` roles.

## Module responsibilities

Each module defines its own use cases, state transitions and resource-level rules. It asks Foundation to evaluate the relevant named capability and scope, resolve configured alçadas, record an audit event, propagate correlation and use provider boundaries. It must not add a broad shared abstraction merely for future reuse.
