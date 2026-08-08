# Issue #43: Foundation Reproducible Baseline Design

## Status

Approved.

## Goals

- Establish a reproducible Foundation database from an empty PostgreSQL
  database using EF Core and Npgsql.
- Make the Foundation model the canonical home for multi-company identity,
  membership, authorization, audit and file metadata contracts.
- Enforce company isolation in both application authorization and PostgreSQL.
- Prove the baseline through public integration tests, a stable schema snapshot
  and CI execution.
- Produce a read-only migration inventory for `cbqoliveira/vinx-erp` without
  importing, executing, changing or depending on its database history.

## Non-goals

- Recreate or bootstrap the legacy schema.
- Implement Paguai, Kanban, HR, Service Desk, Notifications or AI business
  entities, workflows or endpoints.
- Import legacy data.
- Implement login, upload completion, malware scanning, signed downloads,
  payment approval or MCP write actions.
- Introduce a generic repository, persistence abstraction or a second
  application database migration system.

## Canonical-source rules

`FoundationDbContext`, its EF entity configurations and its EF migrations are
the sole canonical source for the business database schema. The migration
assembly is part of the Foundation project and contains the complete ordered
history from the initial baseline onward.

PostgreSQL-specific RLS policies, grants, functions, triggers and append-only
protection are emitted with `migrationBuilder.Sql(...)` in the migration that
introduces the affected object. They are not maintained as standalone SQL
migration files or applied manually. `EnsureCreated`, ad-hoc DDL and
`supabase/migrations` are prohibited for the business schema.

The EF `ModelSnapshot` detects model changes not represented by a migration.
A `pg_dump` schema snapshot is generated only after migrations have run; it is
a reviewed artifact, never an input to schema creation or a competing source
of truth.

Supabase Storage provisioning is provider infrastructure, not business schema.
Its bucket configuration and Storage policies are managed idempotently by the
server-side Storage adapter provisioning path and verified against Supabase
local. Foundation file metadata remains in the EF schema.

## Components and file boundaries

The implementation keeps normal persistence directly on EF Core/Npgsql.

```text
src/Foundation/Vinx.Erp.Foundation/
  Domain/                 Foundation entities and value types
  Identity/               Authenticated ActorContext contract; no login flow
  Authorization/          Capability evaluation and active-company contract
  Audit/                  Audit event contract and append-only writer
  Files/                  File metadata and private-container provisioning
  Observability/          Correlation context and redacted telemetry contract
  Persistence/
    FoundationDbContext.cs
    Configurations/       EF Fluent API mappings and query filters
    Migrations/           EF migrations and ModelSnapshot
tests/public/
  Vinx.Erp.Foundation.UnitTests/
  Vinx.Erp.Foundation.IntegrationTests/
  Vinx.Erp.Foundation.Bdd/
docs/
  inventory/              Read-only legacy classification
  modules/                Versioned module contracts and required template
  runbooks/               Migration, snapshot and local verification commands
```

The API, MCP host and worker use Foundation application services and the same
`FoundationDbContext`; no endpoint, MCP tool or worker owns authorization or
database rules. This issue exposes one read-only capability map for an explicit
active company. It also implements the transport-independent
`AssignRoleToMembership` command needed to prove company-safe role assignment;
no HTTP or MCP mutation endpoint is exposed for that command in this issue.

## Foundation data model

The initial schema uses `foundation` and `audit` PostgreSQL schemas. Business
and configuration rows use UUID identifiers, `timestamptz` timestamps and an
optimistic-concurrency `row_version` stored as a non-null `bigint` starting at
`1`. EF marks it as a concurrency token, includes the original value in update
predicates and a Foundation `SaveChangesInterceptor` sets each modified
entity's value to `original + 1`. A stale write therefore affects zero rows and
becomes a stable conflict result. Database triggers and Npgsql `xmin` are not
used for this token.
`created_by_user_id` and `updated_by_user_id` are required on mutable business
or configuration rows. Critical commands carry a UUID `request_id`; the
idempotency record has a unique key over company, actor, command name and
`request_id`. Immutable technical rows do not receive authorship fields that
have no meaning.

Entity identifiers are application-generated UUIDs with no database default.
`AssignRoleToMembership` is a critical idempotent command: the record stores a
payload hash and serialized stable result. A replay by the same company, actor
and command with the same request and payload returns the stored result without
a second assignment or success audit event; reuse with a different payload
returns an idempotency conflict. Including the actor in the unique key is
intentional so independent actors may use the same client-generated UUID.

Foundation owns these initial entities:

- `groups` and `companies`, with every company assigned to one group.
- `units` and `teams`, each owned by one company; a team belongs to a unit in
  that same company.
- Global `users` and provider-specific `external_identities` linked to users.
- `memberships`, which join one user to one company and represent active
  internal access; `employees` are company-owned records associated with a
  membership where an employment record is needed.
- Company-owned `roles`; global named `capabilities`; role-capability grants
  with exactly one scope value from `self`, `team`, `unit`, `company` or
  `group`; and role assignments from a membership to a role.
- `files` metadata, including company, uploader membership, classification,
  checksum, provider key and quarantine state. It contains no file contents or
  polymorphic business-resource reference. A future module links its resource
  to a file through a module-owned table with a composite company FK.
- Company-scoped idempotency keys.
- `audit.audit_events`, containing actor, requested company, source, action,
  resource type and identifier, outcome, correlation identifier, redacted
  reason/metadata and timestamp. The requested-company UUID is evidence of the
  attempted context rather than a foreign key, so an unknown or unauthorized
  company identifier can still be audited. It is nullable only for the
  `company_context_required` denial, where no company UUID was supplied.

Business and configuration entities use soft deletion with `deleted_at`,
`deleted_by_user_id` and `deleted_reason`. EF query filters exclude deleted
rows. Restores are explicit authorized commands and create audit events.
Unique business keys use partial unique indexes for active rows. Audit events
are append-only, while idempotency rows use documented technical retention and
are never user-soft-deleted.

No Paguai beneficiary, request, payment, Kanban board, HR absence or Service
Desk entity exists in this baseline.

The five scope values are authorization vocabulary in this issue; a grant does
not yet carry a team, unit or group target identifier. The capability map
returns the granted scope types, while target resolution and resource-level
evaluation wait for a module with concrete owned resources.

## Cross-company constraints

Every company-owned entity has a non-null `company_id`. Each such table has a
unique alternate key `(company_id, id)`. Every relation from one company-owned
entity to another uses a composite foreign key containing both the local
`company_id` and the referenced identifier, targeting `(company_id, id)`.

Examples include team-to-unit, employee-to-membership, role-assignment-to-role
and role-assignment-to-membership. The database therefore rejects a row that
combines a member, role, unit, team or other company-owned resource from two
companies, even if an application validation is missed.

`AssignRoleToMembership` is the reference cross-company command. It validates
that the actor may assign roles in the requested company and that both the role
and target membership belong to it. A mismatch is denied before `SaveChanges`
and is audited in the separate denial transaction described below. A resource
unavailable in the active company is treated as not found to avoid
tenant-existence disclosure. The integration suite also bypasses the
application service, attempts the invalid association directly and proves that
the composite FK rejects it. That direct database test proves the final
constraint; the application-command test proves durable denial auditing.

Other constraint failures are handled as integrity failures, recorded with
redacted operational diagnostics and never exposed with raw database details.

## Authorization and RLS transaction flow

Every company-scoped operation supplies one explicit company identifier. The
only future all-authorized mode is read-only; it is not exposed by this
baseline endpoint.

For an application transaction, the server performs this sequence:

1. Authenticate the actor through the server-side identity boundary and
   require a syntactically valid requested company and correlation identifier.
2. Begin a PostgreSQL transaction.
3. Set `app.user_id`, the requested `app.company_id` and
   `app.correlation_id` transaction-locally with parameterized `set_config`
   calls.
4. Validate the actor's active membership and evaluate the named capability
   under the runtime role and RLS.
5. Execute authorized queries or writes, persist the successful audit event in
   the same transaction, then commit.

Transaction-local context is mandatory so a pooled connection cannot retain an
earlier actor or company. Every RLS expression reads settings with
`current_setting(name, true)`; an absent value is `NULL` and denies all rows
rather than raising or opening access. The runtime application database role is
`vinx_runtime LOGIN NOINHERIT NOBYPASSRLS` and is distinct from every table
owner. It receives its password only from server configuration; browser
clients never receive it.

The migration implements this explicit policy matrix:

| Table category | Runtime policy |
|---|---|
| Company-owned tables | Enable and force RLS. `USING` and `WITH CHECK` require the row's `company_id` to equal the transaction-local requested company. |
| `foundation.companies` | Enable and force RLS. Only the row whose `id` equals the requested company is visible. |
| `foundation.groups` | Enable and force RLS. A group is visible only when it owns the requested company. |
| Global `capabilities` | Grant `SELECT` only; no runtime write access. |
| Global `users` and `external_identities` | Grant no runtime table privilege; the authenticated `ActorContext` supplies the resolved internal user identifier. |
| `audit.audit_events` | No RLS. Grant no runtime table privilege; runtime can only execute the append function described below. |

Integration tests execute tenant access under the runtime role, never a table
owner. Future tables must choose one of these categories explicitly in their
module contract and migration.

RLS is a database defense-in-depth boundary against missing tenant predicates
in trusted server code. It is not presented as independent authentication
against a compromised server process that holds the application database
credential. Browser clients never receive this credential or direct data
access.

The first endpoint returns effective capability names and their granted scope
types for the verified actor and one requested company. Its application
boundary accepts a nullable requested-company value solely so it can persist
the authenticated actor's `company_context_required` denial before returning
HTTP 400; authorized evaluation still requires a value. It consumes an
`ActorContext` constructed from an authenticated server-side principal. Public
integration tests use a test authentication scheme; production login and token
validation remain owned by issue #44. The endpoint has no generic resource
evaluator. Persisting all five scope types now allows later modules to define
resource ownership and hierarchy rules without changing the authorization
vocabulary; resource-level evaluation begins only when a module has concrete
resources to evaluate.

The API host calls `AddAuthentication()` and `AddAuthorization()` during
service registration and `UseAuthentication()` then `UseAuthorization()`
before mapping Foundation endpoints. Until issue #44 supplies real token
validation, the production default authentication handler is deliberately
deny-only: it never authenticates a principal and challenges with HTTP 401.
Integration tests replace it only through `ConfigureTestServices`.

The runtime Npgsql connection is supplied only by
`FOUNDATION_RUNTIME_CONNECTION`; absent configuration fails startup by naming
that variable without printing its value. Public host tests supply an
ephemeral runtime connection through their shared test factory.

## Audit and failure semantics

Successful commands write an audit event in the same transaction as their
business state. If that audit write fails, the whole command rolls back and is
reported as an unavailable audited operation; business state never commits
without its required success event.

Denied requests cannot write their event in the rejected business transaction.
After the server reaches a denial decision, it starts a separate short audit
transaction, establishes the verified actor/requested-company/correlation
context with transaction-local settings, and appends the denial event. For
`company_context_required`, it clears the company setting and passes a null
requested-company argument; all other authorization decisions carry the
requested UUID. A narrow
security-definer audit append function is available to the runtime role for
this path so an absent membership cannot prevent recording the attempted
access. `audit.audit_events` and the function share a dedicated non-login owner
without `BYPASSRLS`; the table does not use RLS. The function fixes its
`search_path` to trusted schemas, accepts only the audited fields, inserts one
immutable event and has no update or delete behavior. The migration revokes
all function privileges from `PUBLIC` before granting only `EXECUTE` to the
runtime role.

If denial-event persistence fails, the request remains denied and returns the
stable `audit_unavailable` error. Operational telemetry records the failure
without sensitive payloads. The application role receives `EXECUTE` on the
narrowly granted append function and no direct `INSERT`, `UPDATE` or `DELETE`
permission on `audit.audit_events`. Successful commands call the same function
inside their business transaction; denied requests call it in the separate
transaction. Database privileges and an append-only trigger enforce
immutability for direct SQL performed by any non-owner role.

Audit metadata is structured and redacted. It contains no passwords, tokens,
prompt bodies, medical details or file contents.

## Storage boundary

The server-side storage provisioning boundary exposes only the current use
case: idempotently ensure one private container. Its Supabase adapter creates
or verifies the private bucket and confirms that anonymous reads are denied.
The integration test seeds a fixed non-sensitive object through the admin API,
makes an anonymous GET without inspecting its body, and accepts only local
provider non-success statuses 400, 401, 403 or 404.
The browser receives neither Storage administration credentials nor a
service-role credential.

The EF `files` table records business ownership and security metadata only.
The upload-intent, direct signed upload, completion callback, type/size
validation, malware scan, quarantine release and signed download flows are
outside this issue. Supabase-specific buckets and policies are intentionally
not represented as EF entities or EF business-schema migrations.
Cross-company file access through the provider adapter is tested when the first
upload-intent or download operation gives that boundary an ownership-aware data
operation. This baseline instead proves private provisioning and cross-company
isolation of the EF file metadata.

## Legacy inventory

The legacy repository `cbqoliveira/vinx-erp` is strictly read-only reference
material. The new repository does not execute its migrations, copy its schema
as bootstrap state or alter any legacy file.

The inventory is a versioned Markdown table with these required columns:

| Legacy object or screen | Source path | Candidate module | Decision | Rationale | Dependencies | Follow-up issue |
|---|---|---|---|---|---|---|

`Decision` is exactly one of `preservar`, `adaptar` or `descartar`. Database
discovery covers every legacy table, view, function, policy and bucket. UI
discovery covers every routed screen and every Supabase-backed feature or
service; unreachable vendor/demo template directories may be grouped into one
`descartar` entry per directory when the inventory records evidence that no
route or service references them. Completion requires a non-empty rationale
and either a Foundation/module owner or a follow-up issue for every discovered
item. The inventory is documentation only; no data or DDL is generated from
it.

## Migration, snapshot and CI flow

The PostgreSQL baseline job uses a pinned PostgreSQL container image and a
separate ephemeral database for every execution:

```text
empty PostgreSQL database
  -> bootstrap migration, runtime and audit-owner roles idempotently
  -> dotnet ef migrations has-pending-model-changes
  -> dotnet ef database update
  -> public PostgreSQL and authorization tests
  -> pg_dump schema-only snapshot
  -> normalized snapshot comparison
```

The versioned bootstrap command runs with the ephemeral container administrator
and creates `vinx_migrator LOGIN NOINHERIT NOBYPASSRLS`,
`vinx_runtime LOGIN NOINHERIT NOBYPASSRLS` and
`vinx_audit_owner NOLOGIN NOINHERIT NOBYPASSRLS`. It grants membership of
`vinx_audit_owner` to `vinx_migrator`; because the migrator is
`NOINHERIT`, it receives no audit-owner privilege implicitly, while the
membership's `SET` option permits PostgreSQL `ALTER ... OWNER TO
vinx_audit_owner` checks. The migration temporarily grants that owner
`CREATE` on the trusted `audit` schema, creates all protections, transfers
the audit table and append function, then revokes schema `CREATE` again. The
bootstrap grants both login roles `CONNECT` on the
database, grants `CREATE` on the database only to `vinx_migrator`, and is
idempotent. The production runbook requires the equivalent provider-approved
preflight; credentials come from the environment and deployment execution
remains issue #70.

The EF design-time configuration obtains its ephemeral connection string from
the job environment; it contains no committed secret. `dotnet ef database
update` is the CI database creation path. Both the standalone snapshot writer
and the default snapshot verifier build from their own fresh pinned database,
apply the migrations, and use that container's `pg_dump` with
`--schema-only --no-owner --no-privileges` for the Foundation and audit
schemas. Normalization is limited to the
pinned `pg_dump` version header, `\\restrict`/`\\unrestrict` markers and
volatile dump comments, and is versioned with the verification script. A
snapshot difference fails CI and must be reviewed together with the migration
that caused it. Because the snapshot excludes ACLs, deterministic privilege
tests assert runtime RLS, audit-function execution and absence of direct audit
table DML grants.

The EF migration history is the only creation mechanism. CI applies it with
`dotnet ef database update`; public integration tests apply the same history
programmatically with `Database.MigrateAsync`. Neither path uses
`EnsureCreated`.

The public integration fixture starts a fresh Testcontainers PostgreSQL
instance, creates the three roles, verifies their attributes and migrator
`SET ROLE` ownership capability, migrates with the migration role, then runs
application access through the legitimate runtime login connection. It never
shares a database state with another test class. A separate Supabase-local job
provisions the private bucket through the Storage adapter and runs provider
integration tests; it does not participate in the business-schema snapshot.
The public verifier runs foundation database verification before public .NET
tests. A separate CI Storage job runs only the local-Supabase verifier. A
controlled temporary EF project proves that
`migrations has-pending-model-changes` exits non-zero for actual model drift;
a separate temporary `BrokenMigration` proves that a failed migration is not
recorded in `__EFMigrationsHistory` and reports its migration identifier.

## Testing and error handling

Public tests cover, at minimum:

- Applying all EF migrations to an empty PostgreSQL database.
- Detecting an EF model change that lacks a migration.
- Comparing the deterministic generated snapshot with the approved snapshot.
- Requiring an explicit company for the capability-map operation.
- Denying a user without membership, a capability-less membership and a
  cross-company resource attempt.
- Allowing a correctly scoped membership to read its effective capability map.
- Denying and separately auditing `AssignRoleToMembership` when the role and
  target membership belong to different companies.
- Persisting actor, requested company, outcome and correlation identifier for
  both allowed and denied authorization decisions.
- Rejecting a direct cross-company composite-FK association.
- Rejecting audit update and delete attempts by the runtime role.
- Querying company-owned rows without a tenant predicate under the runtime
  role and returning only rows for the transaction-local company.
- Querying a company-owned table under the runtime role without transaction
  context and returning zero rows without an error.
- Verifying the runtime role can execute the audit append function but has no
  direct audit-table DML privilege.
- Verifying the explicit runtime grants: database `CONNECT`; schema
  `USAGE`; only approved company-table `SELECT`/`INSERT`/`UPDATE`;
  capability `SELECT`; audit-function `EXECUTE`; and no global or audit
  table DML.
- Verifying that `SET LOCAL` context does not survive a transaction on a reused
  connection.
- Proving the Supabase-local provisioning operation is idempotent, the bucket
  is private and anonymous access is denied.
- Rejecting cross-company access to EF file metadata under the runtime role.

The public BDD language is Foundation-specific. Both scenarios remain under
one feature-level `@ignore` until the Task 4 Reqnroll step definitions and API
slice exist; Task 4 removes `@ignore` and makes them executable. They cover a missing
explicit company on the capability map and a cross-company role assignment
that is denied and audited.
No Paguai vocabulary remains in Foundation tests.

Application errors use stable, non-sensitive codes. Missing company context,
missing membership, missing capability, unavailable resource and audit outage
are distinguishable to clients only where doing so cannot disclose another
company's data. Logs and test fixtures contain identifiers and redacted
metadata only.

## Acceptance-criteria mapping

| Issue #43 criterion | Baseline proof |
|---|---|
| Empty CI database applies baseline without error | Pinned PostgreSQL Testcontainers job runs `dotnet ef database update` from an empty database. |
| Schema matches approved snapshot | Same job generates the normalized schema-only dump and compares it with the committed artifact. |
| Cross-company association fails and is audited | `AssignRoleToMembership` denies and audits the mismatch; a separate direct-database test proves the composite FK also rejects it. |
| Every new module declares company owner, version and deletion strategy | Foundation receives an explicit contract version, and `docs/modules/_template.md` requires ownership, company-scope rules, contract version, deletion strategy and migration-note links. A public documentation check rejects a module contract missing a required heading. |
| Authorization and private storage are tested before real data | Public PostgreSQL authorization tests and Supabase-local private Storage adapter tests execute before any business-module data migration. |
| Invalid migration is not considered applied | A clean-database CI run fails at the EF migration command and reports the EF migration identifier; later tests, snapshot publication and homologation artifacts do not run. |

## Deferred work

- Resource-level evaluation semantics for `self`, `team`, `unit`, `company`
  and `group` in business modules with concrete resources.
- Approval-policy and alçada entities until Paguai discovery issue #45 defines
  a present, configurable policy use case. Foundation retains ownership of the
  eventual shared contract.
- Read-only all-authorized views and cache-clearing frontend behavior.
- High-risk PostgreSQL stored commands for payment, medical-document and
  export actions.
- Identity login flows and corporate OIDC/SAML adapter support.
- Full file transfer, scanning, quarantine and signed URL workflows.
- Transactional outbox records, worker dispatch and provider delivery adapters
  until a business slice publishes a concrete event.
- Ownership-aware FileStorage upload and download operations, including
  cross-company adapter denial tests.
- Legacy data migration, if a separately approved module issue requires it.
