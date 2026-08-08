# Foundation module contract

Foundation owns the cross-cutting contracts that every ERP module consumes. It does not own Paguai, Service Desk, Kanban, HR, Notifications or AI business rules.

## Ownership

- Groups, companies, units, teams, users, employees and active memberships.
- Roles, named capabilities and scope grants: `self`, `team`, `unit`, `company` and `group`.
- Configurable approval policies and alçadas. A sole owner approver is data configuration, never a hard-coded module rule.
- Identity and file-storage provider boundaries: `IdentityProvider` and `FileStorage`.
- Durable audit, correlation context, redaction, OpenTelemetry conventions, health checks, idempotency keys and outbox technical contracts.

## Multi-company authorization

A person can hold different memberships, roles and scopes in more than one company. The active context is one company or a read-only all-authorized view. Every command, including an MCP command, provides one explicit `empresa_id`; the server validates membership, capability and scope regardless of frontend visibility.

The frontend receives effective capabilities only to shape menus, routes, buttons, columns and data. It is never the authorization authority. API, MCP and database protections deny forged or cross-company requests.

## Data and audit rules

Business and configuration data use soft delete with `deleted_at`, `deleted_by_user_id` and `deleted_reason`; a restore is authorized and audited. Audit events are append-only and use retention rather than soft delete. Module data remains explicitly company-scoped; an authorized group-wide view is a union, never a tenant-less record.

## Module responsibilities

Each module defines its own use cases, state transitions and resource-level rules. It asks Foundation to evaluate the relevant named capability and scope, resolve configured alçadas, record an audit event, propagate correlation and use provider boundaries. It must not add a broad shared abstraction merely for future reuse.
