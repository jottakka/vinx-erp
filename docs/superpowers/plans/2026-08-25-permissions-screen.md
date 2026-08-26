# Permissions Screen — Implementation Blueprint (single PR, branch `codex/permissions-screen`)

Author: Sol (code-architect), 2026-08-25. Anchored on #211 (slice B: member invite delivery) and #212 (admin screen for people). Deferred explicitly: #211C acceptance/first-login linking, role editing, #305/#306 WhatsApp/Telegram channels.

PR body must carry: (a) the owner's explicit single-PR mandate overriding the >25-files decomposition rule of `AGENTS.md` §Fluxo, (b) the manual-lines overflow justification (~800-line soft budget), (c) the deferred list above.

## 1. Architecture summary

The feature reuses four proven rails end-to-end: capability authorization (`foundation.roles.assign` @ Company, `CapabilityEvaluator` + RLS via `FoundationRlsContext`), the idempotent mutation service shape of `RoleAssignmentService` / `EmployeeDirectoryService` (transaction + RLS context + idempotency record + transactional audit + audited denials), the SECURITY DEFINER narrow-function pattern of `20260816120000_AddEmployeeDirectory.cs` for anything `vinx_runtime` cannot read directly (notably `foundation.users`, from which emails never leave the database), and the owner-invitation state machine (`pending_delivery → pending_provider → provider_in_flight → pending_acceptance | provider_failed | provider_ambiguous`) with its Wolverine durable outbox/worker/DLQ rails. A new ownership role `vinx_permissions_owner` owns the new SQL functions; default `admin`/`member` roles are seeded per company by an idempotent ensure-function called both by migration backfill and by the provisioning service for new companies. The member invitation flow is a faithful tenant-scoped mirror of the owner invitation flow minus the platform-operator/receipt checks, sharing one generalized Supabase invite client. HTTP stays minimal-API under `/foundation` but moves to a new extension class (`AgentEndpoints.cs` precedent) to stop growing the 950-line `Program.cs`. The FE adds a `features/permissions` slice following `people-page.tsx` (capability-filtered company selector, no optimistic updates, requestId idempotency discipline, `refreshBootstrap()` on auth failures) with MUI List/Chips/Select/Dialog primitives only.

### Endpoint table

All routes under the existing `foundation` group (`RequireAuthorization` + `ProtectedRateLimitPolicy`, `X-Correlation-ID` on every response via middleware). Mutations additionally carry `BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true)` + `BrowserBffNoStoreEndpointMetadata`; reads carry `BrowserBffNoStoreEndpointMetadata`.

| Route | Method | Auth / capability | Success | Errors ({code} bodies) |
|---|---|---|---|---|
| `/foundation/companies/{companyId}/members` | GET | `foundation.roles.assign` (SQL fn gate) | 200 `[{membershipId, displayName, roles:[{roleId,name}], capabilities:[name], invitationStatus?}]` | 401 empty; 403 `capability_denied`; 429; 503 `audit_unavailable`. Never returns email. |
| `/foundation/companies/{companyId}/roles` | GET | same | 200 `[{roleId, name, capabilities:[name]}]` | same as above |
| `/foundation/companies/{companyId}/role-assignments` | POST `{requestId, membershipId, roleId}` | membership + `foundation.roles.assign` (existing `RoleAssignmentService`, unwrapped) | 200 `{roleAssignmentId}` (created and replay are indistinguishable — service contract unchanged) | 400 `invalid_request`; 401; 403 `membership_required`/`capability_denied`; 404 `resource_not_found`; 409 `idempotency_conflict`/`integrity_conflict`; 429; 503 `audit_unavailable` |
| `/foundation/companies/{companyId}/role-unassignments` | POST `{requestId, membershipId, roleId}` | same | 200 `{roleAssignmentId, removed: true}` (replay returns stored result) | above plus 409 `last_admin_guard` |
| `/foundation/companies/{companyId}/permission-copies` | POST `{requestId, sourceMembershipId, targetMembershipId}` | same | 200 `{targetMembershipId, appliedRoleIds:[...]}` (no-op identical sets = success without mutation) | 400 `invalid_request` (source==target); 403; 404; 409 `idempotency_conflict`/`last_admin_guard`; 429; 503 |
| `/foundation/companies/{companyId}/invitations` | POST `{requestId, displayName, email}` | membership + `foundation.roles.assign` (SQL fn gate) | 201 `{membershipId, invitationId, invitationStatus:"pending_delivery"}` / 200 replay | 400 `invalid_request`; 401; 403 `capability_denied`; 409 `idempotency_conflict`/`member_conflict`; 429; 503 `member_invitation_unavailable` |

Note on 404 vs 403 for unknown company on reads: reads follow the `EmployeeDirectoryService.ListAsync` convention — unknown/unauthorized company is not in the authorized set → 403 `capability_denied` (no existence oracle). Mutations resolve ids inside the company → 404 `resource_not_found` (existing `RoleAssignmentService` semantics).

`AuthorizationErrorCode` gains one member: `LastAdminGuard` → `"last_admin_guard"` → 409 (extends both switches in `AuthorizationResult.cs`; default arms still throw).

## 2. Exact file list

Root: `/Users/fjll/Documents/ChatGPT/vinx-erp/.worktrees/permissions-screen`

### CREATE — backend

1. `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260825100000_AddCompanyDefaultRoles.cs` (+ `.Designer.cs`) — new role `vinx_permissions_owner`, `foundation.ensure_company_default_roles(uuid)`, backfill over existing companies, grants/policies.
2. `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260825110000_AddCompanyMemberPermissionsRead.cs` (+ `.Designer.cs`) — `foundation.list_company_members_with_roles(uuid)` and `foundation.list_company_roles_with_grants(uuid)`, column grant `SELECT (id, display_name) ON foundation.users TO vinx_permissions_owner`, `EXECUTE` to `vinx_runtime` only.
3. `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260825120000_AddPendingMemberInvitations.cs` (+ `.Designer.cs`) — `foundation.pending_member_invitations` table, `foundation.create_pending_member_invitation(...)` (User+Membership+Employee+invitation+idempotency+audit in one function), `CREATE OR REPLACE` of the members list fn to add `invitation_status`.
4. `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260825130000_AddMemberInvitationWorkerIngress.cs` (+ `.Designer.cs`) — `provider_pending_at`, status check, backlog index, `accept_member_invitation_delivery(uuid, uuid)`; mirrors `20260816130000`.
5. `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260825140000_AddMemberInvitationProviderDelivery.cs` (+ `.Designer.cs`) — six provider columns + full status CHECK, `claim_member_invitation_provider_delivery` and `finalize_member_invitation_provider_delivery`; mirrors `20260817001000`.
6. `src/Foundation/Vinx.Erp.Foundation/Authorization/RoleUnassignmentService.cs` — `UnassignRoleFromMembershipAsync(actor, command)` mirroring `RoleAssignmentService` (CommandName `foundation.roles.unassign`, soft-delete, advisory lock, `last_admin_guard`).
7. `src/Foundation/Vinx.Erp.Foundation/Authorization/UnassignRoleFromMembershipCommand.cs` + `UnassignRoleFromMembershipResult.cs` — records mirroring the assign pair.
8. `src/Foundation/Vinx.Erp.Foundation/Authorization/RolePermissionsCopyService.cs` — `CopyAsync(actor, command)`, CommandName `foundation.roles.copy`; replace-target semantics, no-op success, `last_admin_guard`, source==target → `Invalid`.
9. `src/Foundation/Vinx.Erp.Foundation/Authorization/CopyRolePermissionsCommand.cs` + `CopyRolePermissionsResult.cs` — records.
10. `src/Foundation/Vinx.Erp.Foundation/Authorization/LastAdminGuard.cs` — shared helper `CompanyRetainsRolesAssignAdminAsync(db, companyId, excludeMembershipId)` (one LINQ query joining memberships→role_assignments→role_capabilities→capabilities where name=`foundation.roles.assign`, scope=`company`, all `deleted_at IS NULL`).
11. `src/Foundation/Vinx.Erp.Foundation/Permissions/CompanyPermissionDirectoryService.cs` — `ListMembersAsync(actor, companyId)` / `ListRolesAsync(actor, companyId)`; tx + `FoundationRlsContext.ApplyCommandAsync` + SQL fns; `PostgresException` 42501 `'member permissions capability is required'` → audited denial (`PermissionDenied` state), mirroring `EmployeeDirectoryService.CreateAsync`/`AuditDeniedAsync`.
12. `src/Foundation/Vinx.Erp.Foundation/Permissions/MemberInvitationService.cs` — `InviteAsync(actor, command)`; normalizes (same rules as `EmployeeDirectoryService.TryNormalize` incl. `MailAddress` check), opens tx, `IDbContextOutbox` enrollment + `MemberInvitationDeliveryRequested` publish with `DeliveryOptions.CorrelationId` (`PlatformOrganizationProvisioningService.cs:76-151` pattern), commit, post-commit flush with Warning on deferred flush.
13. `src/Foundation/Vinx.Erp.Foundation/Permissions/MemberInvitationDeliveryRequested.cs` — `public sealed record MemberInvitationDeliveryRequested(Guid InvitationId);`
14. `src/Foundation/Vinx.Erp.Foundation/Permissions/MemberInvitationDeliveryIngressService.cs` + `MemberInvitationIngressPermanentException.cs` — mirror of `Platform/OwnerInvitationDeliveryIngressService.cs`.
15. `src/Foundation/Vinx.Erp.Foundation/Permissions/MemberInvitationProviderDeliveryService.cs` — mirror of `Platform/OwnerInvitationProviderDeliveryService.cs` calling the member claim/finalize fns and the generalized Supabase client.
16. `src/Hosts/Vinx.Erp.WorkerHost/MemberInvitationDeliveryRequestedHandler.cs` — public handler + `MemberInvitationDeliveryFailurePolicyAttribute` (retry 1s/5s/30s on transient `NpgsqlException`, then redacted DLQ via `WorkerDeadLetterRedactionInterceptor`; mirrors `OwnerInvitationDeliveryRequestedHandler.cs` exactly).
17. `src/Hosts/Vinx.Erp.WorkerHost/WorkerMemberInvitationDeadLetterException.cs` — mirror of `WorkerOwnerInvitationDeadLetterException`.
18. `src/Hosts/Vinx.Erp.ApiHost/Permissions/PermissionsEndpoints.cs` — `internal static class PermissionsEndpoints { MapFoundationPermissionsEndpoints(this RouteGroupBuilder foundation) }` with the 6 routes, `PermissionsHttpContract` strict parser (uses `StrictJsonFieldSet`, 8 KiB byte-capped read like the employee POST), `WithMetadata(new JsonRequestBodyContract(typeof(...)))`, `WithName`, `Produces*` per status.
19. `src/Hosts/Vinx.Erp.ApiHost/Models/MemberInvitationCreatedResponse.cs` — `{membershipId, invitationId, invitationStatus}` response model for OpenAPI.
20. `src/Hosts/Vinx.Erp.ApiHost/Models/RoleAssignmentCreatedResponse.cs` — `{roleAssignmentId}`.

### CREATE — tests

21. `tests/public/Vinx.Erp.Foundation.IntegrationTests/CompanyDefaultRolesMigrationTests.cs`
22. `tests/public/Vinx.Erp.Foundation.IntegrationTests/CompanyMemberPermissionsReadEndpointTests.cs`
23. `tests/public/Vinx.Erp.Foundation.IntegrationTests/RoleUnassignmentEndpointTests.cs`
24. `tests/public/Vinx.Erp.Foundation.IntegrationTests/RolePermissionsCopyEndpointTests.cs`
25. `tests/public/Vinx.Erp.Foundation.IntegrationTests/MemberInvitationEndpointTests.cs`
26. `tests/public/Vinx.Erp.Foundation.IntegrationTests/MemberInvitationWorkerIngressFunctionTests.cs`
27. `tests/public/Vinx.Erp.Foundation.IntegrationTests/MemberInvitationProviderDeliveryFunctionTests.cs`

### CREATE — frontend

28. `apps/web/src/features/permissions/permissions-client.ts` — `createPermissionsClient()` gateway: `listMembers`, `listRoles`, `assignRole`, `unassignRole`, `copyPermissions`, `inviteMember`; zod strict schemas; failure union incl. `last_admin_guard`, `member_conflict`, `not_found`; antiforgery for mutations; `mayHaveCommitted` on ambiguous mutation outcomes (pattern: `features/people/employee-directory-client.ts`).
29. `apps/web/src/features/permissions/permissions-page.tsx` — the screen (details in T5).
30. `apps/web/src/features/permissions/permissions-client.test.ts`
31. `apps/web/src/features/permissions/permissions-page.test.tsx`
32. `apps/web/e2e/permissions.spec.ts` — mocked-BFF Playwright spec mirroring `e2e/platform-administration.spec.ts`.

### CREATE — docs

33. `docs/manuals/frontend/permissions.md` — pt-BR manual (model on `docs/manuals/frontend/employee-directory.md`).

### MODIFY

34. `src/Foundation/Vinx.Erp.Foundation/Authorization/AuthorizationResult.cs` — add `LastAdminGuard` to both switches (`"last_admin_guard"`, 409).
35. `src/Foundation/Vinx.Erp.Foundation/Authorization/AuthorizationErrorCode.cs` — add `LastAdminGuard`.
36. `src/Foundation/Vinx.Erp.Foundation/Domain/RoleAssignment.cs` — add `Revoke(string reason, Guid actorUserId, Guid requestId)` mirroring `Membership.Revoke` (idempotent when already deleted).
37. `src/Foundation/Vinx.Erp.Foundation/Persistence/FoundationDbFunctions.cs` — add wrappers + row records: `EnsureCompanyDefaultRolesAsync`, `ListCompanyMembersWithRolesAsync`, `ListCompanyRolesWithGrantsAsync`, `CreatePendingMemberInvitationAsync`, `AcceptMemberInvitationDeliveryAsync`, `ClaimMemberInvitationProviderDeliveryAsync`, `FinalizeMemberInvitationProviderDeliveryAsync` (follow existing `UnwrapProviderExceptionAsync` discipline).
38. `src/Foundation/Vinx.Erp.Foundation/Platform/PlatformOrganizationProvisioningService.cs` — in `ExecuteAsync` after `EnsurePlatformOwnerEmployeeCreateAsync` call `EnsureCompanyDefaultRolesAsync(result.CompanyId!.Value, ...)` inside the same transaction.
39. `src/Foundation/Vinx.Erp.Foundation/Platform/SupabaseAdminOwnerInvitationClient.cs` → renamed `SupabaseAdminInvitationClient.cs` — rename-only generalization: class `SupabaseAdminInvitationClient`, `HttpClientName = "supabase-invitation"`, result types `SupabaseInvitationResult(Kind)`; zero logic changes; `InviteAsync(string inviteeEmail, ...)`.
40. `src/Foundation/Vinx.Erp.Foundation/Platform/OwnerInvitationProviderDeliveryService.cs` — adopt renamed client/types (constructor/reference updates only).
41. `src/Hosts/Vinx.Erp.ApiHost/Program.cs` — DI registrations (`CompanyPermissionDirectoryService`, `RoleUnassignmentService`, `RolePermissionsCopyService`, `MemberInvitationService`); Wolverine `PublishMessage<MemberInvitationDeliveryRequested>().ToPostgresqlQueue("member-invitation-delivery").UseDurableOutbox()`; `AddHttpClient(SupabaseAdminInvitationClient.HttpClientName, ...)` name change; one line `foundation.MapFoundationPermissionsEndpoints();`.
42. `src/Hosts/Vinx.Erp.WorkerHost/Program.cs` — `AddScoped` member ingress/delivery services; `Discovery.IncludeType<MemberInvitationDeliveryRequestedHandler>()`; `ListenToPostgresqlQueue("member-invitation-delivery").UseDurableInbox()`; Supabase HTTP client name change.
43. `infra/database/bootstrap-foundation-roles.sql` — `CREATE ROLE vinx_permissions_owner NOLOGIN NOINHERIT NOBYPASSRLS;` and `GRANT vinx_permissions_owner TO vinx_migrator WITH INHERIT FALSE, SET TRUE;`.
44. `infra/database/grant-wolverine-runtime.sql` — add `vinx_wolverine_queues.wolverine_queue_member_invitation_delivery` (+ `_scheduled`) to the `vinx_worker` table grant list.
45. `tests/public/Vinx.Erp.Foundation.UnitTests/PlatformOperator/SupabaseAdminOwnerInvitationClientTests.cs` → rename `SupabaseAdminInvitationClientTests.cs` (assertions unchanged).
46. `docs/modules/foundation.md` — new section "Permissões de membros" (endpoints, seeded roles, invitation slice, state machine, deferred #211C/#305/#306); bump `Contract version` 1.5 → 1.6.
47. `apps/web/src/shared/routing/app-paths.ts` — catalog entry `{ group: "modules", label: "Permissões", navigation: true, path: "/permissoes" }`.
48. `apps/web/src/app/app-route-manifest.tsx` — `routePresentation["/permissoes"] = { appearanceSlot: "neutral", icon: ManageAccountsOutlinedIcon, render: () => <PermissionsPage /> }` + import.
49. `apps/web/src/shared/routing/app-paths.test.ts`, 50. `apps/web/src/app/app-route-manifest.test.tsx`, 51. `apps/web/src/app/router.test.tsx` — extend pinned route lists (they assert `appRouteManifest.map(path)` equals `APP_PATHS`).

EF snapshot `FoundationDbContextModelSnapshot.cs` is NOT touched: no new DbSets, no model changes (invitation table is SQL-only like `pending_owner_invitations`); `.Designer.cs` files are generated per repo convention.

## 3. Data model

### 3.1 `foundation.ensure_company_default_roles` (migration 20260825100000)

Ownership/bootstrap (mirror of the `vinx_employee_directory_owner` blocks in `20260816120000`):

```
GRANT USAGE, CREATE ON SCHEMA foundation TO vinx_permissions_owner;
GRANT INSERT ON foundation.roles, foundation.role_capabilities TO vinx_permissions_owner;
GRANT SELECT ON foundation.companies, foundation.roles, foundation.role_capabilities,
    foundation.capabilities, foundation.memberships TO vinx_permissions_owner;
GRANT REFERENCES (id) ON foundation.companies TO vinx_permissions_owner;
CREATE POLICY roles_permissions_owner_insert ON foundation.roles FOR INSERT TO vinx_permissions_owner
    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
CREATE POLICY role_capabilities_permissions_owner_insert ON foundation.role_capabilities
    FOR INSERT TO vinx_permissions_owner
    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
CREATE POLICY memberships_permissions_owner_select ON foundation.memberships FOR SELECT
    TO vinx_permissions_owner USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
```

Function (owner `vinx_permissions_owner`, `SECURITY DEFINER`, `SET search_path TO pg_catalog, foundation, pg_temp`):

- Signature: `ensure_company_default_roles(p_company_id uuid) RETURNS void`.
- `PERFORM set_config('app.company_id', p_company_id::text, true)`.
- Seed actor: `SELECT user_id INTO v_seed_actor FROM foundation.memberships WHERE company_id = p_company_id AND deleted_at IS NULL ORDER BY created_at, id LIMIT 1;` if none → `RAISE EXCEPTION 'company has no active membership' USING ERRCODE='23514'` (used for `created_by_user_id`/`updated_by_user_id`, FK-safe; no actor requirements — this is a pure additive seed, unlike `ensure_platform_owner_employee_create` which is receipt-bound).
- For each spec `('admin', ARRAY['foundation.roles.assign','foundation.employees.create','foundation.files.upload','foundation.files.confirm'])` and `('member', ARRAY['foundation.files.upload'])`:
  - If no live role with that `(company_id, name)` exists (`deleted_at IS NULL`): insert role (`gen_random_uuid()`, `request_id = gen_random_uuid()`, actor = `v_seed_actor`), then insert one `role_capabilities` row per capability (`scope='company'`) via `INSERT ... SELECT` joining `foundation.capabilities` by name, `GET DIAGNOSTICS` count must equal the expected array length else `RAISE ... '23514'` (mirrors the owner-role count guard in `provision_platform_organization`), `ON CONFLICT (company_id, role_id, capability_id, scope) WHERE deleted_at IS NULL DO NOTHING`.
  - If a live role with that name already exists (company created a custom `admin`/`member` before this PR): leave it and its grants untouched — the ensure function is additive only and never widens a pre-existing role.
- The `owner` role created by provisioning is never read or modified here.
- No audit event (no business mutation by a user; the provisioning-path audit remains `platform.organization.provision`). No idempotency record — idempotency is structural (`ON CONFLICT DO NOTHING`).
- Backfill in the same migration, executed as the owner role:
  `SET ROLE vinx_permissions_owner; SELECT foundation.ensure_company_default_roles(company.id) FROM foundation.companies AS company WHERE company.deleted_at IS NULL; RESET ROLE;`
- Then `REVOKE ALL ON FUNCTION ... FROM PUBLIC, vinx_migrator; GRANT EXECUTE ... TO vinx_runtime; REVOKE CREATE ON SCHEMA foundation FROM vinx_permissions_owner;` (runtime grant is for the provisioning path).
- Runtime call site: `PlatformOrganizationProvisioningService.ExecuteAsync` immediately after `EnsurePlatformOwnerEmployeeCreateAsync` — both inside the same caller transaction as `provision_platform_organization`; company gets `owner` (unchanged) + `admin` + `member`.

Down: fail closed if any seeded grant rows exist (`company_id IN (SELECT id FROM companies)` with role names admin/member and capability names in the seeded sets) — mirror the `DO $block$ ... 23514` guards; drop policies/grants/role membership expectations; roles created by the function are NOT dropped (state exists guard prevents downgrade instead).

### 3.2 `foundation.pending_member_invitations` + create function (migration 20260825120000)

```
CREATE TABLE foundation.pending_member_invitations (
    id uuid PRIMARY KEY,
    membership_id uuid NOT NULL REFERENCES foundation.memberships(id) ON DELETE RESTRICT,
    user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
    company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
    status varchar(30) NOT NULL CHECK (status = 'pending_delivery'),
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_pending_member_invitations_membership_id UNIQUE (membership_id)
);
ALTER TABLE ... ENABLE/FORCE ROW LEVEL SECURITY;
CREATE POLICY pending_member_invitations_permissions_owner_insert
    ON foundation.pending_member_invitations FOR INSERT TO vinx_permissions_owner
    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
```

(`UNIQUE(membership_id)` — not `user_id` as in the owner table — because a user row is reserved per invitation; one live invitation lifecycle per membership is the correct single-flight domain key. Mirrors `uq_pending_owner_invitations_user_id` in spirit.)

`foundation.create_pending_member_invitation(p_request_id uuid, p_company_id uuid, p_display_name text, p_email text, p_payload_hash text) RETURNS TABLE (status text, membership_id uuid, user_id uuid, employee_id uuid, invitation_id uuid)` — owner `vinx_permissions_owner` (needs, additionally: `GRANT INSERT ON foundation.users, foundation.memberships, foundation.employees, foundation.idempotency_records TO vinx_permissions_owner;` + matching RLS insert policies keyed on `app.company_id`, `GRANT USAGE ON SCHEMA audit` + `audit.append_event` EXECUTE from `vinx_audit_owner`, all mirroring the employee-directory grants). Body mirrors `create_employee_directory_entry` verbatim where applicable:

- Parameter validation identical (22023 on bad payload/displayName>200/email>320).
- Capability gate: same EXISTS block but `capability.name = 'foundation.roles.assign'` → `'member invitation capability is required'` 42501.
- `pg_advisory_xact_lock(hashtextextended(actor|company|request, 0))`; idempotency lookup with `command_name = 'foundation.member.invite'`; replay/idempotency_conflict returns from `result_json` (`membershipId`,`userId`,`employeeId`,`invitationId`).
- Inserts `users` / `memberships` / `employees` with the exact column lists of `create_employee_directory_entry` (this is the "reuse the employee-directory SQL path" requirement — same insert semantics, single transaction, one receipt; calling the employee function itself would record the wrong idempotency command, wrong capability gate, and leave the invitation row outside the receipt).
- Plus: `INSERT INTO foundation.pending_member_invitations (id, membership_id, user_id, company_id, status) VALUES (gen_random_uuid(), v_membership_id, v_user_id, p_company_id, 'pending_delivery') RETURNING id INTO v_invitation_id;`
- Idempotency record + `audit.append_event(actor,'company',company,'web','foundation.member.invite','member_invitation',v_invitation_id,'success',correlation,NULL,'{}')`.
- `REVOKE FROM PUBLIC, vinx_migrator; GRANT EXECUTE TO vinx_runtime;`

Also in this migration: `CREATE OR REPLACE FUNCTION foundation.list_company_members_with_roles` adding the `invitation_status` output column via `LEFT JOIN foundation.pending_member_invitations AS invitation ON invitation.membership_id = membership.id` (returns the live status text or NULL). Single documented exception to one-migration-one-function-owner, needed because the table must exist first; both definitions remain SECURITY DEFINER under `vinx_permissions_owner`.

### 3.3 Read functions (migration 20260825110000, before 3.2 in numbering — no invitation join yet)

- `list_company_members_with_roles(p_company_id uuid) RETURNS TABLE (membership_id uuid, display_name text, role_ids uuid[], role_names text[], capability_names text[], invitation_status text)` — capability gate (`foundation.roles.assign`, 42501 `'member permissions capability is required'`), active memberships of the company, `display_name` from `foundation.users` (column grant `SELECT (id, display_name) ON foundation.users TO vinx_permissions_owner` — email is never granted to this role), arrays aggregated over `role_assignments ⋈ roles` and `role_assignments ⋈ roles ⋈ role_capabilities ⋈ capabilities` (`scope='company'`, all `deleted_at IS NULL`), `ORDER BY min(display_name)`; every column reference alias-qualified (42702 trap).
- `list_company_roles_with_grants(p_company_id uuid) RETURNS TABLE (role_id uuid, name text, capability_names text[])` — same gate; all live roles of the company + granted capability names, ordered by name.
- `REVOKE FROM PUBLIC, vinx_migrator; GRANT EXECUTE TO vinx_runtime;`

### 3.4 Worker ingress (20260825130000) — mirror of 20260816130000

- `ALTER TABLE foundation.pending_member_invitations ADD COLUMN provider_pending_at timestamptz NULL;` + `ck_pending_member_invitations_status` (`pending_delivery`⇒NULL / `pending_provider`⇒NOT NULL) + `ix_pending_member_invitations_provider_backlog ... WHERE status='pending_provider'`.
- `GRANT SELECT, UPDATE (status, provider_pending_at) ON foundation.pending_member_invitations TO vinx_worker_owner;` + permissive read/update policies to `vinx_worker_owner` (as owner migration does); `GRANT USAGE ON SCHEMA audit` + `audit.append_event` EXECUTE for `vinx_worker_owner`.
- `accept_member_invitation_delivery(p_invitation_id uuid, p_correlation_id uuid) RETURNS TABLE (status text, provider_pending_at timestamptz, transitioned boolean)` — identical structure to `accept_owner_invitation_delivery` except: instead of the `organization_provisioning_receipts` cross-checks, validate `membership_id` still references an active membership of `invitation.company_id` (`deleted_at IS NULL`) else `RAISE 'member invitation membership is inconsistent' 23514`; `pending_delivery → pending_provider` transition, audit `member_invitation.delivery.ingress` (source `'worker'`, resource_type `'member_invitation'`).
- `REVOKE ... GRANT EXECUTE TO vinx_worker;`

### 3.5 Provider delivery (20260825140000) — mirror of 20260817001000

- Same six columns (`provider_attempt_id uuid, provider_started_at timestamptz, provider_completed_at timestamptz, provider_issuer text, provider_subject uuid, provider_failure_reason varchar(50)`), the same full `ck_pending_member_invitations_status` six-state CHECK and `ck_pending_member_invitations_provider_subject_not_empty`, `GRANT UPDATE (provider_*) TO vinx_worker_owner`.
- `claim_member_invitation_provider_delivery(p_invitation_id, p_correlation_id) RETURNS TABLE (status text, attempt_id uuid, member_email text, claimed boolean)` — identical logic; email read from `foundation.users` (relies on the existing column grant `SELECT (id, email) ON foundation.users TO vinx_worker_owner` from 20260817001000 — do not re-grant, and this migration's Down must not revoke it).
- `finalize_member_invitation_provider_delivery(p_invitation_id, p_attempt_id, p_correlation_id, p_outcome text, p_provider_issuer text, p_provider_subject uuid, p_failure_reason text) RETURNS TABLE (status text, finalized boolean)` — identical validation matrix; success → `pending_acceptance` recording `provider_issuer` + `provider_subject`; `failed` → `provider_failed`/`provider_rejected`; `ambiguous` → `provider_ambiguous`; audit `member_invitation.delivery.provider` with outcome success/failure/unknown.
- `REVOKE ... GRANT EXECUTE TO vinx_worker;`

### 3.6 What the invite-time Supabase response records (for #211C parity)

Exactly what the owner flow records, nothing more: `provider_issuer` = `SupabaseAuthOptions.NormalizedIssuer`; `provider_subject` = the invited user's Supabase auth UUID, accepted only when the `POST /invite` response user object passes every check in `TryGetInvitedSubject` (`id` parses, `aud` equals configured audience, `email` equals invited email case-insensitively, `is_anonymous` is false, `banned_until`/`deleted_at` missing-or-null) — otherwise the attempt is `provider_ambiguous` and nothing is recorded. Plus the attempt bookkeeping columns (`provider_attempt_id`, `provider_started_at/completed_at`, `provider_failure_reason`). Never recorded: email, tokens, raw bodies — in envelope, logs, or audit. #211C will later link first login by `issuer + subject` against `external_identities`, identical to the owner path.

## 4. Task breakdown

T1–T4 are backend, mutually independent except where noted; T5 depends on T2+T3+T4 contracts; T6 depends on T5.

**T1 — Default roles migration + provisioning hook** (files 1, 34–38 partially: `EnsureCompanyDefaultRolesAsync` wrapper + provisioning call, 43, 21)
- Add `vinx_permissions_owner` to bootstrap SQL; migration 20260825100000; `FoundationDbFunctions.EnsureCompanyDefaultRolesAsync`; `PlatformOrganizationProvisioningService.ExecuteAsync` hook.
- Verify: `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~CompanyDefaultRolesMigrationTests|FullyQualifiedName~PlatformOrganizationProvisioning"` and `./scripts/verify-foundation-database.sh`.
- Parallelizable with T3, T4 (touch disjoint files except `FoundationDbFunctions.cs` — merge note: land wrappers in one commit or rebase).

**T2 — Read services + endpoints** (files 2, 11, part of 18, 22)
- Read migration, `CompanyPermissionDirectoryService`, GET members/roles routes in `PermissionsEndpoints.cs`, DI registration.
- Verify: `... --filter "FullyQualifiedName~CompanyMemberPermissionsReadEndpointTests"`; OpenAPI sanity `--filter "FullyQualifiedName~OpenApiInferenceContractPinTests"`.
- Parallelizable with T3/T4 after T1 lands its migration numbering (independent code; only shares `PermissionsEndpoints.cs` and `FoundationDbFunctions.cs` — coordinate).

**T3 — Unassign + copy services, last-admin guard, mutation endpoints** (files 6–10, 20, part of 18/34/35/36/41, 23, 24)
- `RoleAssignment.Revoke`, `LastAdminGuard`, both services (advisory lock `SELECT pg_advisory_xact_lock(hashtextextended(companyId, 0))` inside the tx before staging, then guard = "would target lose its only `foundation.roles.assign` grant while no other active membership holds one"), `AuthorizationErrorCode.LastAdminGuard`, assign/unassign/copy routes. Assign route wraps the untouched `RoleAssignmentService` (confirmed: assign only inserts rows — it can never create a zero-admin post-state, so no guard there).
- Verify: `... --filter "FullyQualifiedName~RoleUnassignmentEndpointTests|FullyQualifiedName~RolePermissionsCopyEndpointTests|FullyQualifiedName~RoleAssignmentServiceIntegrationTests"`.
- Fully parallelizable with T1/T4.

**T4 — Invitation backend + worker** (files 3–5, 12–17, 39, 40, 42, 44, 45, 25–27, part of 18/41)
- Migrations 20260825120000/30000/40000, `MemberInvitationService` (enqueue + outbox), Wolverine message + queue registration in both hosts, worker handler + DLQ, client rename refactor, `grant-wolverine-runtime.sql`, invite route.
- Verify: `... --filter "FullyQualifiedName~MemberInvitation"` then `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore --filter "FullyQualifiedName~SupabaseAdminInvitationClientTests"` and `dotnet test tests/public/Vinx.Erp.Hosts.IntegrationTests/Vinx.Erp.Hosts.IntegrationTests.csproj --no-restore`.
- Parallelizable with T1/T3.

**T5 — FE client, page, routes** (files 28–31, 47–51)
- `permissions-client.ts` (failure kinds incl. `last_admin_guard`, `member_conflict`; CSRF; mayHaveCommitted).
- `permissions-page.tsx`: company `TextField select` filtered by `hasCompanyCapability(company, "foundation.roles.assign")` (people-page.tsx:89-95 pattern); zero eligible companies → inert `Alert severity="info"` ("Nenhuma empresa disponível para gerenciar permissões.", platform-organization-page pattern); members `List` with `Chip` per role + invitation chip (`pending_delivery|pending_provider|provider_in_flight` → "Convite pendente"; `provider_failed` → warning "Falha na entrega"; `provider_ambiguous` → warning "Entrega incerta"); per-member actions open `Dialog`s: Papéis (role `Select` + chips with delete for unassign), Copiar de (member `Select` excluding self, confirm), Convidar membro (`react-hook-form` + zod, pt-BR messages identical in tone to `people-page.tsx`); `useQuery` ["permissions-members", companyId] / ["permissions-roles", companyId], `useMutation` with `invalidateQueries` on success (no optimistic updates); fresh `requestId = crypto.randomUUID()` per attempt, retry with SAME requestId after ambiguous failure; 401/403 → `refreshBootstrap()` + neutralize view; inline `Alert`s + `useFeedback().notify` for successes; `last_admin_guard` → inline error "A empresa precisa de ao menos um administrador ativo."; `member_conflict` → "Este e-mail já possui cadastro."
- Routes: `/permissoes` nav-visible in modules group; manifest icon `ManageAccountsOutlinedIcon` (from existing `@mui/icons-material` dep — no new dependency).
- Verify: `npm --workspace apps/web run test -- --run src/features/permissions src/app/app-route-manifest.test.tsx src/shared/routing/app-paths.test.ts src/app/router.test.tsx` then `npm --workspace apps/web run lint && npm --workspace apps/web run build`.

**T6 — E2E + docs** (files 32, 33, 46)
- Playwright spec with `page.route` mocks: bootstrap (operator as company admin with `foundation.roles.assign`), antiforgery, GET members/roles, POST invite/assign/unassign/copy; journeys: invite 201 receipt message; non-manager inert alert; last_admin_guard inline alert; retry-after-ambiguous reuses requestId (assert captured request bodies).
- Docs: foundation.md section + contract bump; `permissions.md` manual.
- Verify: `(cd apps/web && npx playwright test permissions)`; `npm run test:no-direct-supabase`; `git diff --check`.

Final: `./scripts/verify-public.sh` once (multiple public projects + shared DB infra changed — allowed trigger), then CI.

## 5. Test matrix

| Behavior | Test |
|---|---|
| ensure fn seeds admin(4 caps)+member(1 cap) idempotently; re-run no-ops | `CompanyDefaultRolesMigrationTests.Seeds_default_roles_idempotently_per_company` |
| Backfill covers pre-existing companies; provisioning seeds new companies | `CompanyDefaultRolesMigrationTests.Backfills_existing_companies_and_provisioning_seeds_new_company` |
| Owner role grants untouched (still 5 incl. `employees.create`) | `CompanyDefaultRolesMigrationTests.Owner_role_grants_are_unchanged` |
| Pre-existing custom `admin` role is not widened | `CompanyDefaultRolesMigrationTests.Custom_role_with_reserved_name_is_left_untouched` |
| Function ownership/privileges (SECURITY DEFINER, pinned search_path, EXECUTE only `vinx_runtime`) | `CompanyDefaultRolesMigrationTests.Function_security_boundary` |
| GET members happy path: roles + capability names, ordered | `CompanyMemberPermissionsReadEndpointTests.List_returns_members_with_roles_and_capabilities` |
| GET responses never contain email (raw JSON scan) | `CompanyMemberPermissionsReadEndpointTests.Member_payloads_never_expose_email` |
| GET 401 unauthenticated / 403 no membership / 403 no capability | `...Requires_authentication_membership_and_capability` |
| GET roles happy path | `CompanyMemberPermissionsReadEndpointTests.List_returns_roles_with_grants` |
| Assign happy path + replay + idempotency_conflict + 404 unknown role/membership (existing service via new route) | `RoleUnassignmentEndpointTests.Assign_endpoint_contract_is_preserved` (covers `RoleAssignmentService` wrap) |
| Unassign removes assignment, audits `foundation.roles.unassign`, replay returns stored result | `RoleUnassignmentEndpointTests.Unassign_soft_deletes_replays_and_audits` |
| Unassign last admin → 409 `last_admin_guard`, state unchanged, denial audited | `RoleUnassignmentEndpointTests.Unassign_rejects_removing_the_last_admin` |
| Copy replaces target set; identical sets → no-op success; source==target → 400; audit `foundation.roles.copy` | `RolePermissionsCopyEndpointTests.Copy_replaces_target_roles_and_audits` |
| Copy that would strip last admin → 409 `last_admin_guard` | `RolePermissionsCopyEndpointTests.Copy_rejects_zero_admin_post_state` |
| Copy replay w/ different body → 409 `idempotency_conflict` | `RolePermissionsCopyEndpointTests.Copy_replay_with_divergent_payload_conflicts` |
| Invite 201 body shape + rows created in one tx (user/membership/employee/invitation) + no ExternalIdentity/role assignment | `MemberInvitationEndpointTests.Invite_creates_reserved_graph_and_pending_invitation` |
| Invite replay 200 same ids; divergent payload 409; email collision 409 `member_conflict` | `MemberInvitationEndpointTests.Invite_idempotency_and_conflicts` |
| Outbox envelope contains only invitationId + correlation in `wolverine_queue_member_invitation_delivery` | `MemberInvitationEndpointTests.Invite_publishes_redacted_durable_envelope` |
| Worker ingress `pending_delivery→pending_provider` + audit; replay no dup | `MemberInvitationWorkerIngressFunctionTests.Ingress_transitions_once_and_audits` |
| Claim single-flight exposes email only to winner; finalize success records issuer+subject → `pending_acceptance` | `MemberInvitationProviderDeliveryFunctionTests.Claim_and_finalize_mirror_owner_state_machine` |
| finalize failed/ambiguous states; function privileges (EXECUTE only `vinx_worker`) | `MemberInvitationProviderDeliveryFunctionTests.Finalize_failure_states_and_function_security` |
| Supabase client rename is behavior-preserving | `Vinx.Erp.Foundation.UnitTests/SupabaseAdminInvitationClientTests` (unchanged assertions) |
| FE client schema/failure mapping incl. `last_admin_guard`, CSRF header, mayHaveCommitted | `apps/web/src/features/permissions/permissions-client.test.ts` |
| Page: capability filter, inert alert, role chips, invitation chip, dialogs, retry-same-requestId, refresh on 401/403, guard alert | `apps/web/src/features/permissions/permissions-page.test.tsx` |
| Route catalog/manifest/router pins include `/permissoes` | `app-paths.test.ts`, `app-route-manifest.test.tsx`, `router.test.tsx` |
| E2E journeys with mocked BFF | `apps/web/e2e/permissions.spec.ts` |

BDD/architecture tests: not required — no existing step definitions touch these routes and no architecture rule pins endpoint location (verify via CI; add a step only if `AuthorizationStepDefinitions` semantics change — they do not).

## 6. Risks / open questions (with resolution)

1. **Queue provisioning is a deploy step** — `member-invitation-delivery` tables are created by `db-apply` (Wolverine) and granted by `grant-wolverine-runtime.sql`; skipping it wedges invites at `pending_delivery`. Resolution: PR description calls out the deploy runbook order (migrate → db-apply both hosts → grant script); tests provision via `WolverineStorageDeployment` exactly like the owner queue.
2. **Re-invite of a failed invitation** — email uniqueness (`ix_users_email`) makes a second invite of the same address return `member_conflict`. Resolution: FE surfaces a clear inline message; delivery reconciliation for failed/ambiguous invitations stays with #211C (owner flow has the same retention semantics — documented in foundation.md).
3. **No role auto-assignment at invite** — invited members hold zero capabilities until a manager assigns/copies roles (least privilege; keeps this PR decoupled from role editing). The screen makes the "Sem papéis" state visually obvious with an explicit empty chip state. If the owner wants a default `member` assignment, it is a 3-line addition to `create_pending_member_invitation` — flag in PR description, not implemented.
4. **`last_admin_guard` race** — two concurrent demotions could both pass a naive check. Resolution: `pg_advisory_xact_lock` on the company id inside unassign/copy transactions serializes guard+mutation (same idiom as the employee function).
5. **plpgsql 42702** — every new function alias-qualifies all column references (the migrations in §3 spell this out); integration tests execute all functions against real Postgres, so a slip fails fast.
6. **`Program.cs` Wolverine registration drift** — ApiHost publish rule and WorkerHost listener must both exist or messages strand in the outbox; mitigation: both edits land in T4 as one reviewable unit, plus the outbox integration test asserts the envelope and worker test consumes it.
7. **Client rename blast radius** — `SupabaseAdminOwnerInvitationClient` → `SupabaseAdminInvitationClient` renames types, file, HTTP client name, and its unit test file with zero logic change; owner-flow behavior is pinned by the untouched unit-test assertions plus `OwnerInvitationProviderDeliveryFunctionTests`, which must stay green unmodified.
8. **Single PR size** — ~51 files exceeds the 25-file rule; PR body carries the owner's explicit override, the >800-manual-lines justification, and the deferred-scope list (top of this document). Review aid: commits are structured T1→T6 so the single review can follow task-sized diffs.
