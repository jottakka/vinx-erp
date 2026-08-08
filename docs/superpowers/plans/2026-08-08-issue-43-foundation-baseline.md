# Issue #43 Foundation Baseline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Deliver an EF Core-first, reproducible and multi-company-safe Foundation baseline, including the first read-only capability-map API slice.

**Architecture:** FoundationDbContext, its configurations and EF migrations are the only canonical business-schema history. PostgreSQL RLS, grants and immutable audit functions are SQL within the initial EF migration. The API supplies a verified actor and explicit company context; application checks give stable errors while composite foreign keys and RLS remain database defenses in depth.

**Tech Stack:** .NET 10, EF Core 10.0.10, Npgsql EF Core provider 10.0.3, EFCore.NamingConventions 10.0.1, PostgreSQL postgres:17.9-alpine, ASP.NET Core Minimal APIs, xUnit, Reqnroll, Testcontainers, Supabase CLI local stack and built-in HttpClient.

## Global Constraints

- Work only on codex/issue-43-foundation-baseline; never write directly to main.
- Read only public project files and tests/public; do not read, list or execute quality/private.
- EF migrations are the sole business-schema creation path. Do not use EnsureCreated, ad-hoc DDL or supabase/migrations for the Foundation schema.
- vinx_runtime is LOGIN NOINHERIT NOBYPASSRLS; vinx_migrator is LOGIN NOINHERIT NOBYPASSRLS; vinx_audit_owner is NOLOGIN NOINHERIT NOBYPASSRLS.
- Bootstrap grants vinx_audit_owner membership to vinx_migrator with INHERIT FALSE and SET TRUE, so migrations can transfer audit ownership without inherited audit privileges.
- The pinned PostgreSQL image for Testcontainers and snapshot generation is postgres:17.9-alpine.
- Every company-owned relation uses a composite (company_id, id) alternate key and composite foreign key; application UUIDs have no database default.
- Every command has one explicit company. Browser code receives neither a PostgreSQL credential nor a Supabase service-role credential.
- Soft delete is standard for mutable business/configuration entities. Audit is append-only; idempotency uses technical retention.
- Logs, fixtures, test data and Markdown contain no secrets, tokens, medical content, prompt bodies or file contents.
- Supabase Storage uses a server-side HttpClient adapter with environment-only service-role options. It is provider provisioning, not EF business schema.
- The production role-bootstrap procedure is documented and preflighted here; production credentials and deployment execution remain issue #70.
- The legacy cbqoliveira/vinx-erp repository is reference-only and strictly read-only.
- External code review is an execution policy. Do not add review-agent code, model configuration or review plumbing to the product.

---

### Task 1: Establish public Foundation contracts and documentation checks

**Model tier:** GPT Luna, max — mechanical documentation and inventory structure only.

**Files:**
- Create: docs/modules/_template.md.
- Create: docs/inventory/legacy-foundation-inventory.md.
- Create: tests/public/Vinx.Erp.ArchitectureTests/ModuleContractDocumentationTests.cs.
- Modify: docs/modules/foundation.md.
- Modify: tests/public/Vinx.Erp.Foundation.Bdd/Features/Authorization.feature.

**Interfaces:**
- Consumes: docs/superpowers/specs/2026-08-08-issue-43-foundation-baseline-design.md.
- Produces: Foundation contract version 1.0, a public module-documentation contract and executable Foundation BDD vocabulary.

- [ ] **Step 1: Write the failing documentation-contract test**

Create ModuleContractDocumentationTests.cs:

~~~
private static readonly string[] RequiredHeadings =
[
    "## Ownership",
    "## Company scope",
    "## Contract version",
    "## Deletion strategy",
    "## Migration notes"
];

[Theory]
[InlineData("docs/modules/foundation.md")]
[InlineData("docs/modules/_template.md")]
public void Module_contract_contains_required_headings(string relativePath)
{
    var text = File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));
    Assert.All(RequiredHeadings, heading => Assert.Contains(heading, text, StringComparison.Ordinal));
}

private static string RepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vinx.Erp.sln")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName
        ?? throw new InvalidOperationException("Could not locate Vinx.Erp.sln.");
}
~~~

Run:

~~~bash
dotnet test tests/public/Vinx.Erp.ArchitectureTests --filter FullyQualifiedName~ModuleContractDocumentationTests
~~~

Expected: FAIL because the template and required Foundation headings do not exist.

- [ ] **Step 2: Define the template, Foundation contract and BDD**

Create docs/modules/_template.md with exact headings:

~~~markdown
# Module contract: module name

## Ownership

## Company scope

## Contract version

## Deletion strategy

## Migration notes
~~~

Every copy of the template replaces `module name`; the committed template keeps
that instructional placeholder. Add these headings to Foundation: version 1.0;
all commands carry one company; mutable entities use soft delete;
audit.audit_events is append-only; first migration is
InitialFoundationBaseline.

Rewrite Authorization.feature with feature-level @ignore and @foundation tags
and without payment vocabulary. Task 4 removes @ignore only after its Reqnroll
step definitions and API slice exist. Include these scenarios:

~~~gherkin
@ignore @foundation
Feature: Foundation authorization

Scenario: Capability map without an explicit company is denied
  Given Ana is authenticated
  When she requests her capability map without a company identifier
  Then the request is denied with code "company_context_required"
  And the authorization denial is auditable with Ana as the actor

Scenario: Role assignment across companies is denied and audited
  Given Ana may assign roles in company "Vinx Obras"
  And a role belongs to company "Vinx Obras"
  And a membership belongs to company "Vinx Serviços"
  When Ana assigns the role to the membership using company "Vinx Obras"
  Then the command is denied with code "resource_not_found"
  And the authorization denial contains the requested company and correlation identifier
~~~

- [ ] **Step 3: Create the read-only inventory structure**

Create the exact header below and state that Decision is exactly preservar, adaptar or descartar. State completion requires non-empty rationale plus an owner or issue for every discovered item.

~~~markdown
| Legacy object or screen | Source path | Candidate module | Decision | Rationale | Dependencies | Follow-up issue |
|---|---|---|---|---|---|---|
~~~

- [ ] **Step 4: Run green checks and commit**

~~~bash
dotnet test tests/public/Vinx.Erp.ArchitectureTests --filter FullyQualifiedName~ModuleContractDocumentationTests
./scripts/verify-public.sh
git add docs/modules docs/inventory tests/public/Vinx.Erp.ArchitectureTests tests/public/Vinx.Erp.Foundation.Bdd/Features/Authorization.feature
git commit -m "docs: define foundation baseline contracts"
~~~

Expected: focused and public checks PASS. Commit contains docs and public documentation checks only.

### Task 2: Add EF Core packages and deterministic PostgreSQL test infrastructure

**Model tier:** GPT Terra, high — EF tooling, database roles and integration infrastructure.

**Files:**
- Create: infra/database/bootstrap-foundation-roles.sql.
- Create: tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationPostgresFixture.cs.
- Create: tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationPostgresFixtureTests.cs.
- Modify: Directory.Packages.props.
- Modify: src/Foundation/Vinx.Erp.Foundation/Vinx.Erp.Foundation.csproj.
- Modify: tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj.
- Modify: affected packages.lock.json files.
- Delete: tests/public/Vinx.Erp.Foundation.IntegrationTests/PostgresContainerFixture.cs and PostgresContainerTests.cs.

**Interfaces:**
- Consumes: Docker and public integration tests.
- Produces: FoundationPostgresFixture with StartAsync, OpenAdministratorConnectionAsync, MigratorConnectionString, RuntimeConnectionString and a migrator SET ROLE capability proof.

- [ ] **Step 1: Write the failing fixture contract test**

~~~csharp
[Fact]
public async Task StartAsync_creates_the_three_non_superuser_roles_and_migrator_can_set_audit_owner()
{
    await using var fixture = new FoundationPostgresFixture();
    await fixture.StartAsync();

    await using var connection = await fixture.OpenAdministratorConnectionAsync();
    await using var command = new NpgsqlCommand(
        "SELECT rolname FROM pg_roles WHERE rolname = ANY (@roles) ORDER BY rolname", connection);
    command.Parameters.AddWithValue("roles", new[]
    {
        "vinx_audit_owner", "vinx_migrator", "vinx_runtime"
    });

    await using var reader = await command.ExecuteReaderAsync();
    var roles = new List<string>();
    while (await reader.ReadAsync()) roles.Add(reader.GetString(0));

    Assert.Equal(["vinx_audit_owner", "vinx_migrator", "vinx_runtime"], roles);
    Assert.True(await fixture.MigratorCanSetAuditOwnerAsync());
}
~~~

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter FullyQualifiedName~FoundationPostgresFixtureTests
~~~

Expected: FAIL because the fixture does not exist.

- [ ] **Step 2: Add pinned packages**

Add these exact central versions:

~~~xml
<PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.10" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.10" />
<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
<PackageVersion Include="EFCore.NamingConventions" Version="10.0.1" />
<PackageVersion Include="Npgsql" Version="10.0.3" />
~~~

Foundation references Microsoft.EntityFrameworkCore, Npgsql.EntityFrameworkCore.PostgreSQL and EFCore.NamingConventions. Reference Microsoft.EntityFrameworkCore.Design with PrivateAssets=all.

- [ ] **Step 3: Implement bootstrap and fixture**

bootstrap-foundation-roles.sql idempotently creates vinx_migrator LOGIN
NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE,
vinx_runtime LOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE,
and vinx_audit_owner NOLOGIN NOINHERIT NOBYPASSRLS. On PostgreSQL 17 it uses
`GRANT vinx_audit_owner TO vinx_migrator WITH INHERIT FALSE, SET TRUE`, grants
CONNECT on the current database to
both login roles, grants CREATE on that database only to vinx_migrator, and
does not grant inherited audit-object privileges. The fixture test queries
pg_roles for LOGIN, INHERIT and BYPASSRLS attributes, verifies database grants,
then connects as vinx_migrator and proves SET ROLE vinx_audit_owner succeeds.

FoundationPostgresFixture pins the image and never writes generated test passwords to logs:

~~~csharp
private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
    .WithImage("postgres:17.9-alpine")
    .Build();

public Task StartAsync();
public Task<NpgsqlConnection> OpenAdministratorConnectionAsync();
public string MigratorConnectionString { get; }
public string RuntimeConnectionString { get; }
~~~

StartAsync executes the committed bootstrap script as container administrator,
sets migrator/runtime test passwords with parameterized administrator commands
from process memory, and builds both
legitimate LOGIN connection strings. MigrateAsync uses only the migrator
connection; runtime tests use only RuntimeConnectionString.

- [ ] **Step 4: Run green test, remove the scalar probe and commit**

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter FullyQualifiedName~FoundationPostgresFixtureTests
dotnet restore --force-evaluate
dotnet restore --locked-mode
./scripts/verify-public.sh
git add Directory.Packages.props src/Foundation/Vinx.Erp.Foundation tests/public/Vinx.Erp.Foundation.IntegrationTests infra/database
git commit -m "build: add foundation ef database harness"
~~~

Expected: fixture test and public gate PASS; only the new fixture replaces the scalar probe.

### Task 3: Build the Foundation EF model, initial migration and database security baseline

**Model tier:** GPT Terra, xhigh — cross-company constraints, RLS, grants, audit immutability and migration safety.

**Files:**
- Create: src/Foundation/Vinx.Erp.Foundation/Domain/{Group,Company,Unit,Team,User,ExternalIdentity,Membership,Employee,Role,Capability,RoleCapability,RoleAssignment,FileRecord,IdempotencyRecord}.cs.
- Create: src/Foundation/Vinx.Erp.Foundation/Domain/{CapabilityScope,AuditOutcome,AuditSource}.cs.
- Create: src/Foundation/Vinx.Erp.Foundation/Audit/AuditEvent.cs.
- Create: src/Foundation/Vinx.Erp.Foundation/Persistence/{FoundationDbContext,FoundationDesignTimeDbContextFactory}.cs.
- Create: src/Foundation/Vinx.Erp.Foundation/Persistence/Interceptors/RowVersionInterceptor.cs.
- Create: src/Foundation/Vinx.Erp.Foundation/Persistence/Configurations/*.cs.
- Create: generated src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/*_InitialFoundationBaseline.cs, designer and FoundationDbContextModelSnapshot.cs.
- Create: tests/public/Vinx.Erp.Foundation.IntegrationTests/{FoundationSchemaTests,CrossCompanyConstraintTests,RlsPolicyTests,AuditPrivilegeTests,RowVersionTests}.cs.

**Interfaces:**
- Consumes: FoundationPostgresFixture and the three roles from Task 2.
- Produces: FoundationDbContext(DbContextOptions<FoundationDbContext>), application-generated UUID entities, RowVersionInterceptor, foundation/audit schemas and audit.append_event.

- [ ] **Step 1: Write failing database behavior tests**

Use these expected objects before their implementation:

~~~csharp
await fixture.MigrateAsync();
await using var administrator = fixture.CreateAdministratorDbContext();

var companyA = new Company(Guid.NewGuid(), groupId, "Vinx Obras", actorId, requestId);
var companyB = new Company(Guid.NewGuid(), groupId, "Vinx Serviços", actorId, requestId);
var roleA = new Role(Guid.NewGuid(), companyA.Id, "Operator", actorId, requestId);
var membershipB = new Membership(Guid.NewGuid(), companyB.Id, userId, actorId, requestId);
var invalid = new RoleAssignment(Guid.NewGuid(), companyA.Id, roleA.Id, membershipB.Id, actorId, requestId);
~~~

Seed fixtures and execute the direct invalid association through
CreateAdministratorDbContext so `FORCE ROW LEVEL SECURITY` does not hide the
foreign-key proof behind a missing tenant context. Assert SaveChangesAsync
fails with PostgreSQL SQL state 23503. Add tests that runtime with no context
returns zero company rows, runtime set to company A returns only A, runtime
company A cannot read company B file metadata, and direct runtime UPDATE/DELETE
on audit.audit_events fails.

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter "FullyQualifiedName~FoundationSchemaTests|FullyQualifiedName~CrossCompanyConstraintTests|FullyQualifiedName~RlsPolicyTests|FullyQualifiedName~AuditPrivilegeTests|FullyQualifiedName~RowVersionTests"
~~~

Expected: FAIL because entities, context and migration do not exist.

- [ ] **Step 2: Implement entities and exact mappings**

Map groups, companies, units, teams, users, external identities, memberships, employees, roles, capabilities, role capabilities, role assignments, files, idempotency records and audit events. Do not add an outbox entity.

Use schema/table mapping and relations below:

| Entity | Table | Required key or relation |
|---|---|---|
| Unit | foundation.units | alternate key (company_id, id) |
| Team | foundation.teams | (company_id, unit_id) to units(company_id, id) |
| Membership | foundation.memberships | active unique (company_id, user_id), alternate (company_id, id) |
| Employee | foundation.employees | (company_id, membership_id) to memberships |
| Role | foundation.roles | active unique (company_id, name), alternate (company_id, id) |
| RoleAssignment | foundation.role_assignments | company-scoped FKs to role and membership |
| RoleCapability | foundation.role_capabilities | role FK, global capability FK, scope check |
| FileRecord | foundation.files | (company_id, uploader_membership_id) to memberships |
| IdempotencyRecord | foundation.idempotency_records | unique (company_id, actor_user_id, command_name, request_id) |
| AuditEvent | audit.audit_events | nullable requested company has no FK; null is valid only for company_context_required denial |

Use UseSnakeCaseNamingConvention(), HasDefaultSchema("foundation"), query filters where DeletedAt is null, partial unique indexes filtered by deleted_at IS NULL, and non-null bigint row_version as an EF concurrency token. RowVersionInterceptor changes modified rows from original + 1. Do not use xmin or a row-version trigger.

- [ ] **Step 3: Generate and secure the first migration**

Set FOUNDATION_MIGRATOR_CONNECTION to the fixture migrator connection and run:

~~~bash
dotnet ef migrations add InitialFoundationBaseline \
  --project src/Foundation/Vinx.Erp.Foundation \
  --startup-project src/Hosts/Vinx.Erp.ApiHost \
  --output-dir Persistence/Migrations
~~~

Edit that generated migration only. Its migrationBuilder.Sql calls must create
schemas, revoke PUBLIC access, enumerate grants, enable and force RLS, and
define policies using:

~~~sql
NULLIF(current_setting('app.company_id', true), '')::uuid
~~~

The migration grants vinx_runtime database CONNECT, USAGE on schemas
foundation and audit, SELECT/INSERT/UPDATE only on company-owned tables required
by the capability map and role-assignment command, SELECT only on
foundation.files for the metadata-isolation proof, SELECT only on
foundation.capabilities, and EXECUTE only on audit.append_event. It grants no
file INSERT/UPDATE/DELETE, no direct DML on audit.audit_events,
foundation.users or foundation.external_identities, and no global-table DML.
Tests query information_schema and
has_table_privilege/has_function_privilege to prove this exact matrix.

The migration creates all audit objects and their protection while the
migrator still owns them, temporarily grants vinx_audit_owner CREATE on the
trusted audit schema, transfers audit.audit_events and audit.append_event with
`ALTER ... OWNER TO vinx_audit_owner`, then revokes that schema CREATE grant.
The membership's SET option satisfies PostgreSQL's owner-transfer check;
do not switch current role before transferring an object still owned by the
migrator.

Create audit.append_event as SECURITY DEFINER with SET search_path = audit,
pg_catalog. Revoke PUBLIC function execution, grant EXECUTE only to
vinx_runtime, grant no direct audit table DML, and create a trigger rejecting
audit UPDATE and DELETE by non-owner roles before the ownership transfer.
Function arguments are actor, nullable requested company, source, action, resource
type, resource identifier, outcome, correlation, reason and JSONB metadata; it
inserts one row and returns void.

- [ ] **Step 4: Finish fixture migration support**

Implement MigrateAsync using MigratorConnectionString and
Database.MigrateAsync. Expose CreateAdministratorDbContext for fixture setup
and direct constraint proofs, CreateMigratorDbContext only for migration-owner
proofs, and CreateRuntimeDbContext for tenant/RLS behavior; register
RowVersionInterceptor on all three. The design-time factory requires
FOUNDATION_MIGRATOR_CONNECTION and names that variable when absent without
printing a connection string.

- [ ] **Step 5: Run green database proofs and commit**

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter "FullyQualifiedName~FoundationSchemaTests|FullyQualifiedName~CrossCompanyConstraintTests|FullyQualifiedName~RlsPolicyTests|FullyQualifiedName~AuditPrivilegeTests|FullyQualifiedName~RowVersionTests"
dotnet ef migrations has-pending-model-changes --project src/Foundation/Vinx.Erp.Foundation --startup-project src/Hosts/Vinx.Erp.ApiHost
./scripts/verify-public.sh
git add src/Foundation/Vinx.Erp.Foundation tests/public/Vinx.Erp.Foundation.IntegrationTests
git commit -m "feat: add secure foundation database baseline"
~~~

Expected: focused tests PASS and EF exits 0 because all model changes are migrated.

### Task 4: Implement authorization, idempotent role assignment, auditing and capability-map API

**Model tier:** GPT Terra, xhigh — authorization flow, audit failure semantics and API integration.

**Files:**
- Create: src/Foundation/Vinx.Erp.Foundation/Identity/ActorContext.cs.
- Create: src/Foundation/Vinx.Erp.Foundation/Authorization/{AuthorizationErrorCode,AuthorizationResult,EffectiveCapability,CapabilityMapService,AssignRoleToMembershipCommand,AssignRoleToMembershipResult,RoleAssignmentService}.cs.
- Create: src/Foundation/Vinx.Erp.Foundation/Audit/{AuditEventData,AuditAppender}.cs.
- Create: src/Foundation/Vinx.Erp.Foundation/Observability/CorrelationContext.cs.
- Create: tests/public/Vinx.Erp.Foundation.Bdd/StepDefinitions/AuthorizationStepDefinitions.cs.
- Create: src/Hosts/Vinx.Erp.ApiHost/Authentication/DeferredAuthenticationHandler.cs.
- Create: tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationApiFactory.cs.
- Create: tests/public/Vinx.Erp.Foundation.IntegrationTests/{AuthorizationEndpointTests,AuditTransactionTests,RlsSessionContextTests,RoleAssignmentServiceIntegrationTests}.cs.
- Modify: src/Hosts/Vinx.Erp.ApiHost/Program.cs.
- Modify: tests/public/Vinx.Erp.Foundation.IntegrationTests/HealthEndpointTests.cs.
- Modify: tests/public/Vinx.Erp.Foundation.Bdd/Features/Authorization.feature.
- Modify: tests/public/Vinx.Erp.Foundation.Bdd/Vinx.Erp.Foundation.Bdd.csproj.

**Interfaces:**
- Consumes: FoundationDbContext, audit.append_event and the Task 3 test fixture.
- Produces: GET /foundation/capabilities?companyId={uuid} and transport-independent RoleAssignmentService.AssignRoleToMembershipAsync.

- [ ] **Step 1: Write failing integration, API and BDD tests**

Use these public contracts:

~~~csharp
public sealed record ActorContext(Guid UserId, Guid CorrelationId);
public sealed record EffectiveCapability(string Name, IReadOnlySet<CapabilityScope> Scopes);
public sealed record AssignRoleToMembershipCommand(
    Guid CompanyId, Guid RoleId, Guid MembershipId, Guid RequestId);

public Task<AuthorizationResult<IReadOnlyList<EffectiveCapability>>> GetCapabilitiesAsync(
    ActorContext actor, Guid? companyId, CancellationToken cancellationToken);

public Task<AuthorizationResult<AssignRoleToMembershipResult>> AssignRoleToMembershipAsync(
    ActorContext actor, AssignRoleToMembershipCommand command, CancellationToken cancellationToken);
~~~

Assign behavior is database-backed and receives no fake unit-test seam. RoleAssignmentServiceIntegrationTests asserts same request/same payload yields one assignment and one success audit; changed payload yields idempotency_conflict; foreign-company role/membership yields resource_not_found and denial audit. API tests assert missing company -> company_context_required, absent membership/capability -> denial, and authorized actor receives capability names and scopes. AuthorizationStepDefinitions uses the same test host and turns both Task 1 scenarios into executable Reqnroll steps.

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter "FullyQualifiedName~AuthorizationEndpointTests|FullyQualifiedName~AuditTransactionTests|FullyQualifiedName~RlsSessionContextTests|FullyQualifiedName~RoleAssignmentServiceIntegrationTests"
dotnet test tests/public/Vinx.Erp.Foundation.Bdd --filter "Category=foundation"
~~~

Expected: FAIL because contracts, services and endpoint do not exist.

- [ ] **Step 2: Implement local context, audit and stable errors**

Before each company-scoped Foundation transaction execute parameterized calls:

~~~sql
SELECT set_config('app.user_id', @user_id, true);
SELECT set_config('app.company_id', @company_id, true);
SELECT set_config('app.correlation_id', @correlation_id, true);
~~~

AuditAppender calls audit.append_event within the active transaction.
RoleAssignmentService opens a separate transaction, applies the same context
and calls AuditAppender for every denial. Failed success auditing rolls back
business state; failed denial auditing returns audit_unavailable while the
operation remains denied.

For a missing-company capability request, CapabilityMapService starts the
separate denial-audit transaction with user and correlation context, clears
`app.company_id` transaction-locally, and appends
`company_context_required` with a null requested-company UUID. It returns the
stable denial only after that audit succeeds; audit failure returns
audit_unavailable.

AuthorizationResult has only: company_context_required, membership_required, capability_denied, resource_not_found, idempotency_conflict and audit_unavailable. It never returns database diagnostics.

- [ ] **Step 3: Implement capability map and critical command**

CapabilityMapService reads only the actor's active membership in the requested company and groups active role grants by capability name with distinct scopes. It does not resolve scope targets.

RoleAssignmentService checks membership, foundation.roles.assign capability, role and target membership under the requested company. It stores a SHA-256 payload hash and serialized stable result in the idempotency record keyed by company, actor, command name and request. A matching replay returns the stored result without another assignment/audit; a changed hash returns idempotency_conflict.

- [ ] **Step 4: Add API route and test-only authentication**

Register Foundation services and map:

~~~csharp
app.MapGet("/foundation/capabilities", async (
    Guid? companyId,
    ClaimsPrincipal principal,
    CapabilityMapService service,
    CancellationToken cancellationToken) =>
{
    var actor = ActorContext.FromAuthenticatedPrincipal(principal);
    var result = await service.GetCapabilitiesAsync(actor, companyId, cancellationToken);
    return result.ToMinimalApiResult();
}).RequireAuthorization();
~~~

Before building the app, call AddAuthentication() with a production
`FoundationDeferred` default scheme and AddAuthorization(); after building it,
call UseAuthentication() and UseAuthorization() before mapping the Foundation
route. DeferredAuthenticationHandler always returns NoResult and challenges
with HTTP 401; it never constructs a principal. Production login/token
validation remains issue #44.

Bind the runtime Npgsql connection only from
FOUNDATION_RUNTIME_CONNECTION environment configuration and fail startup with
the variable name, never the value. Register scoped FoundationDbContext with
UseNpgsql, UseSnakeCaseNamingConvention and RowVersionInterceptor; register
CapabilityMapService, RoleAssignmentService and AuditAppender as scoped
services. FoundationApiFactory supplies the
fixture's runtime connection, installs a test-only authentication scheme with
ConfigureTestServices and actor/correlation claims, and is also used by the
existing HealthEndpointTests. No test scheme exists in production
registration.

Remove @ignore from Authorization.feature, add the Reqnroll step-definition file, and reference the API host plus Microsoft.AspNetCore.Mvc.Testing from the BDD project. The BDD steps exercise the endpoint and RoleAssignmentService integration fixture rather than duplicating authorization logic.
The BDD project references the public IntegrationTests project solely to reuse
FoundationPostgresFixture and FoundationApiFactory; it does not copy their
container or authentication setup.

- [ ] **Step 5: Run green checks and commit**

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter "FullyQualifiedName~AuthorizationEndpointTests|FullyQualifiedName~AuditTransactionTests|FullyQualifiedName~RlsSessionContextTests|FullyQualifiedName~RoleAssignmentServiceIntegrationTests"
dotnet test tests/public/Vinx.Erp.Foundation.Bdd --filter "Category=foundation"
./scripts/verify-public.sh
git add src/Foundation/Vinx.Erp.Foundation src/Hosts/Vinx.Erp.ApiHost tests/public/Vinx.Erp.Foundation.Bdd tests/public/Vinx.Erp.Foundation.IntegrationTests
git commit -m "feat: add foundation authorization slice"
~~~

Expected: PASS; no API write endpoint or MCP handler is introduced.

### Task 5: Add migration, snapshot and CI verification gates

**Model tier:** GPT Terra, high — reproducible schema validation and CI safety.

**Files:**
- Create: scripts/verify-foundation-database.sh.
- Create: scripts/generate-foundation-schema-snapshot.sh.
- Create: scripts/verify-ef-negative-guards.sh.
- Create: docs/database/foundation-schema.snapshot.sql.
- Modify: scripts/verify-public.sh.
- Modify: .github/workflows/ci.yml.
- Modify: docs/runbooks/{public-verification,local-development,foundation-database}.md.

**Interfaces:**
- Consumes: role bootstrap SQL, Foundation migrations, postgres:17.9-alpine and committed snapshot.
- Produces: CI-safe database verification, real negative EF guards and an explicit snapshot-write command.

- [ ] **Step 1: Write a failing snapshot comparison**

Create the snapshot with a deliberate distinct comment and implement an initial diff:

~~~bash
diff -u docs/database/foundation-schema.snapshot.sql "$normalized_snapshot"
~~~

Run:

~~~bash
./scripts/verify-foundation-database.sh
~~~

Expected: FAIL with unified diff and no credential output.

- [ ] **Step 2: Implement deterministic verification**

Each of verify-foundation-database.sh and generate-foundation-schema-snapshot.sh starts and removes its own fresh postgres:17.9-alpine database, executes role bootstrap, exports FOUNDATION_MIGRATOR_CONNECTION only to child commands, applies the current migration history, and generates its own dump. Neither script consumes a database started by the other.

verify-foundation-database.sh uses set -euo pipefail and runs:

~~~bash
dotnet ef migrations has-pending-model-changes --project src/Foundation/Vinx.Erp.Foundation --startup-project src/Hosts/Vinx.Erp.ApiHost
dotnet ef database update --project src/Foundation/Vinx.Erp.Foundation --startup-project src/Hosts/Vinx.Erp.ApiHost
~~~

Both commands resolve FoundationDesignTimeDbContextFactory from the Foundation
assembly, so they do not start ApiHost or require
FOUNDATION_RUNTIME_CONNECTION. The verifier must not manufacture a runtime
credential for a migration-only operation.

generate-foundation-schema-snapshot.sh executes in the pinned container:

~~~bash
pg_dump --schema-only --no-owner --no-privileges --schema=foundation --schema=audit
~~~

It removes only the pinned dump header, \restrict/\unrestrict markers and volatile comments. The standalone --write switch updates docs/database/foundation-schema.snapshot.sql; the default writes only a temporary comparison file.

verify-ef-negative-guards.sh creates a disposable minimal EF project whose generated model snapshot contains only DriftEntity.Id while its active DriftContext also maps DriftEntity.Name. It asserts dotnet ef migrations has-pending-model-changes exits nonzero and output contains pending changes. It then creates a second disposable migration named BrokenMigration containing migrationBuilder.Sql("SELECT 1 / 0"), asserts dotnet ef database update fails with BrokenMigration in output, and queries __EFMigrationsHistory to prove BrokenMigration was not recorded. The script removes both temporary projects and databases in traps.

- [ ] **Step 3: Regenerate, wire CI and commit**

~~~bash
./scripts/generate-foundation-schema-snapshot.sh --write
./scripts/verify-foundation-database.sh
./scripts/verify-ef-negative-guards.sh
./scripts/verify-public.sh
git add scripts .github/workflows/ci.yml docs/database docs/runbooks
git commit -m "ci: verify foundation migrations and schema snapshot"
~~~

The exact verify-public.sh order is npm ci, web lint/format/test/build, no-direct-Supabase check, dotnet tool restore, dotnet restore --locked-mode, dotnet format, dotnet build --no-restore, verify-foundation-database.sh, verify-ef-negative-guards.sh, then the tests/public loop. CI verify-public invokes only this script. The separate CI verify-storage job invokes only verify-supabase-storage.sh. Document that model-pending guards the current tree, the controlled drift project proves its negative behavior, and BrokenMigration proves a failed migration is absent from history before later snapshot/test work.

Expected: both verifier and public gate PASS.

### Task 6: Provision and verify the private Supabase Storage container

**Model tier:** GPT Terra, high — provider boundary, private provisioning and live local integration.

**Files:**
- Create: src/Foundation/Vinx.Erp.Foundation/Files/{FileStorage,SupabaseStorageOptions,SupabaseFileStorage}.cs.
- Create: tests/public/Vinx.Erp.Foundation.IntegrationTests/Storage/{SupabaseStorageFixture,SupabaseFileStorageTests}.cs.
- Create: scripts/verify-supabase-storage.sh.
- Modify: src/Hosts/Vinx.Erp.ApiHost/Program.cs.
- Modify: scripts/verify-public.sh.
- Modify: .github/workflows/ci.yml.
- Modify: docs/runbooks/{local-development,public-verification}.md.

**Interfaces:**
- Consumes: server-only SupabaseStorageOptions and built-in HttpClient.
- Produces: FileStorage.EnsurePrivateContainerAsync and a public local-Supabase gate.

- [ ] **Step 1: Write failing provider integration tests**

~~~csharp
public interface FileStorage
{
    Task EnsurePrivateContainerAsync(CancellationToken cancellationToken);
}

public sealed record SupabaseStorageOptions(
    Uri BaseUri, string ServiceRoleKey, string ContainerId);
~~~

Tests carry `[Trait("Category", "SupabaseStorage")]`, call
EnsurePrivateContainerAsync twice, inspect the bucket through Storage admin API
and assert public is false. They upload a fixed ASCII test object with admin
credentials, make anonymous object GET without reading or returning its body,
assert any non-success status from the accepted local-provider set 400, 401,
403 or 404, then delete the object in cleanup.

~~~bash
./scripts/verify-supabase-storage.sh
~~~

Expected: FAIL because script, adapter and tests do not exist.

- [ ] **Step 2: Implement narrow HttpClient provisioning**

Register SupabaseFileStorage as a typed client with `AddHttpClient`; it uses
only the injected HttpClient, never a Supabase SDK or a manually constructed
client. It sends the key in Authorization Bearer and apikey headers and never
logs it. It POSTs:

~~~http
POST /storage/v1/bucket

{"id":"vinx-foundation-private","name":"vinx-foundation-private","public":false}
~~~

The request uses the validated ContainerId option; the baseline host and tests
set it to the fixed `vinx-foundation-private` value shown above.

Any non-success create response is followed by
GET /storage/v1/bucket/vinx-foundation-private and succeeds only when that GET
succeeds and `public` is false; this covers the provider's duplicate-create
400/409 variants without trusting an error string. Other outcomes throw an
exception containing only HTTP status and container ID. Host configuration
rejects empty URI/key/container without printing the key. Do not add uploads,
downloads, signed URLs or file-content APIs.

- [ ] **Step 3: Implement local verifier and CI job**

verify-supabase-storage.sh first runs npm ci, then starts Supabase through the
project-local CLI with `npx supabase start`, suppressing command output so the
CLI cannot print generated credentials. It captures rather than prints only
API_URL and SERVICE_ROLE_KEY from `npx supabase status -o env`, exports them as
SUPABASE_STORAGE_URL and SUPABASE_STORAGE_SERVICE_ROLE_KEY, runs
`dotnet test` filtered to `Category=SupabaseStorage`, stops Supabase, unsets
both variables in a trap and never prints the key or response body. Add one
dedicated CI verify-storage job after verify-public; that job runs only this
storage script. Modify the default tests/public loop to use
`--filter "Category!=SupabaseStorage"`, so it never requires Supabase local.

- [ ] **Step 4: Run green provider checks and commit**

~~~bash
./scripts/verify-supabase-storage.sh
git add src/Foundation/Vinx.Erp.Foundation src/Hosts/Vinx.Erp.ApiHost tests/public/Vinx.Erp.Foundation.IntegrationTests scripts .github/workflows/ci.yml docs/runbooks
git commit -m "feat: provision private storage container"
~~~

Expected: bucket provisioning is idempotent, anonymous read has an accepted non-success result with no content inspected, and no browser credential exists.

### Task 7: Complete the read-only legacy inventory and final documentation evidence

**Model tier:** GPT Luna, max — mechanical read-only inventory and documentation cross-checks.

**Files:**
- Modify: docs/inventory/legacy-foundation-inventory.md.
- Modify: docs/runbooks/foundation-database.md.
- Modify: docs/modules/foundation.md.
- Modify: tests/public/Vinx.Erp.ArchitectureTests/ModuleContractDocumentationTests.cs.

**Interfaces:**
- Consumes: Task 1 inventory format and a read-only checkout of cbqoliveira/vinx-erp.
- Produces: one traceable preservar/adaptar/descartar decision for every discovered legacy database and UI item.

- [ ] **Step 1: Write the failing inventory-completion check**

Assert the inventory contains this exact declaration:

~~~markdown
Inventory completion: every discovered table, view, function, policy, bucket, routed screen and Supabase-backed feature has a classified row.
~~~

~~~bash
dotnet test tests/public/Vinx.Erp.ArchitectureTests --filter FullyQualifiedName~LegacyInventory
~~~

Expected: FAIL because inventory completion is not declared.

- [ ] **Step 2: Discover legacy source without changing it**

~~~bash
legacy_dir="$(mktemp -d)"
legacy_ref="$(git ls-remote https://github.com/cbqoliveira/vinx-erp.git HEAD | awk '{print $1}')"
git clone --quiet --no-checkout https://github.com/cbqoliveira/vinx-erp.git "$legacy_dir"
git -C "$legacy_dir" checkout --quiet --detach "$legacy_ref"
~~~

Use rg restricted to this disposable clone to enumerate database objects and route/service references. Record the resolved SHA. Do not run migrations/services, alter clone files or import data.

- [ ] **Step 3: Classify every item and document preflight**

Add one row for every table, view, function, policy, bucket, routed screen and Supabase-backed feature, including source path, candidate module, Portuguese decision, concrete rationale, dependencies and module owner/issue. Group an unreachable vendor/demo directory only after rg proves no route/service references it, and include that evidence in rationale.

Add the exact completion declaration. Document legacy SHA, discovery commands, no-DDL/no-data rule, three fixed roles, postgres:17.9-alpine, snapshot command and issue #70 production preflight boundary.

- [ ] **Step 4: Run green documentation checks and commit**

~~~bash
dotnet test tests/public/Vinx.Erp.ArchitectureTests --filter "FullyQualifiedName~ModuleContractDocumentationTests|FullyQualifiedName~LegacyInventory"
./scripts/verify-public.sh
git add docs/inventory docs/modules/foundation.md docs/runbooks/foundation-database.md tests/public/Vinx.Erp.ArchitectureTests
git commit -m "docs: inventory legacy foundation candidates"
~~~

Expected: PASS; final commit contains documentation evidence and its public check only.

## Execution order and review gates

Execute Tasks 1 through 7 serially in numeric order; no implementation tasks
run in parallel. This avoids overlapping edits to Program.cs,
verify-public.sh and ci.yml. Tasks 1–5 and 7 end with focused red/green evidence
and scripts/verify-public.sh; Task 6 ends with
scripts/verify-supabase-storage.sh because CI runs Storage in its separate
verify-storage job. Every task ends with a scoped commit and external review
before the next task.

## Plan self-review

- **Spec coverage:** Tasks 1/7 cover contracts and legacy inventory; Tasks 2/3 cover EF, roles, RLS, composite keys, concurrency, deletion and audit immutability; Task 4 covers explicit-company authorization, idempotency, correlation and audit failure; Task 5 covers empty-database migration, snapshot and CI; Task 6 covers private Storage.
- **Deferred work remains deferred:** target-level scopes, all-authorized views, high-risk commands, production login, upload/scanning/signed URLs, outbox and legacy data migration are absent.
- **Source ownership:** Task 3 alone creates EF business migrations; Task 5 creates a derived review snapshot; Task 6 provisions provider state only through its server-side boundary.
