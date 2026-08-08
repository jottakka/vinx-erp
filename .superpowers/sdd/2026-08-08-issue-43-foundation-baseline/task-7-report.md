# Task 7 — legacy foundation inventory evidence

## Scope

- Issue: #43, foundation baseline.
- Branch: `codex/issue-43-foundation-baseline`.
- Baseline commit: `c3c0131912837b0e3d30c756ab060bdb715330f6`.
- Approved design: `docs/superpowers/specs/2026-08-08-issue-43-foundation-baseline-design.md`.
- Legacy repository: `https://github.com/cbqoliveira/vinx-erp.git`.
- Resolved legacy HEAD: `2ccb0b40fa38925b8aa1bd32a38ade12d7edab47`.

Only public documentation and the public documentation-contract test were in
scope. No runtime code, migration, script, CI configuration or provider state
was changed.

## TDD evidence

The exact completion assertion was added to
`tests/public/Vinx.Erp.ArchitectureTests/ModuleContractDocumentationTests.cs`
before the inventory was populated:

> Inventory completion: every discovered table, view, function, policy, bucket, routed screen and Supabase-backed feature has a classified row.

The first filter spelling selected no tests and was discarded as an invalid
RED attempt. The valid RED was:

```text
dotnet test tests/public/Vinx.Erp.ArchitectureTests --filter FullyQualifiedName~LegacyInventory
```

Evidence from the valid run:

- Exit code: `1`.
- One test matched: `LegacyInventoryDeclaresCompletion`.
- Failure: `Assert.Contains() Failure: Sub-string not found`.
- Result: `Failed: 1, Passed: 0, Skipped: 0, Total: 1`.
- The failure occurred at the new completion assertion before any inventory
  rows existed.

After the incremental inventory patches, the same focused command passed with
`Failed: 0, Passed: 1, Skipped: 0, Total: 1`.

## Legacy read-only evidence

The detached clone was created in a disposable `mktemp -d` directory, resolved
from remote `HEAD`, cloned with `--no-checkout`, checked out detached at the
resolved SHA, and verified with `git rev-parse HEAD`. The discovery commands
used `rg` only from inside that explicit clone. The clone was strictly
read-only: no legacy migration, service, DDL or data command was run, and no
provider state was accessed.

Recorded discovery totals:

| Category | Evidence |
|---|---|
| Tables | 106 declarations, 102 unique logical tables |
| Views | 0 declarations |
| Functions | 74 declarations, 60 unique logical functions |
| Policies | 204 declarations, 176 unique logical name-and-target policies |
| Buckets | 6 storage buckets |
| Routed screens | 234 route declarations, 233 unique paths after the duplicate `/ui-accordion` declaration was aggregated |
| Supabase services | 33 `src/services` files |
| Supabase Edge Functions | 8 `supabase/functions/*/index.ts` files |
| Direct integration candidates | 7 explicitly traced `src` files; the motivational-text scan candidate is classified `descartar` because it is not a functional Supabase-backed feature |

The reference authorization roles were recorded as `anon`, `authenticated`
and `service_role`. The future isolated verification image is
`postgres:17.9-alpine`. A future schema-only boundary is
`pg_dump --schema-only --no-owner --no-privileges --schema=foundation
--schema=audit`; no snapshot or data export was produced by Task 7. Production
preflight remains outside this task and belongs to issue #70.

## Classification evidence

`docs/inventory/legacy-foundation-inventory.md` contains separate sections for
tables/views, functions, policies, buckets, routed screens and Supabase-backed
features. Every listed logical item has a Portuguese `preservar`, `adaptar` or
`descartar` decision, a non-empty rationale, source path, dependency note and
Foundation/module owner or follow-up issue `#43`.

Duplicate declarations were aggregated in the source cell by logical identity:

- Tables: repeated declarations such as `cargos`, `cargo_modulos`,
  `tarefas` and `usuario_modulos` retain all source references.
- Functions: repeated declarations retain all source references.
- Policies: repeated declarations with the same policy name and target retain
  all source references; the 204 declarations therefore produce 176 logical
  rows.
- Routes: the repeated `/ui-accordion` path is represented once.

No view row is required because the scoped scan produced zero logical views;
that zero result is recorded in the inventory. No data, DDL or migration output
was generated.

## Documentation adjustments

- Moved effective-capabilities wording under `## Company scope` in
  `docs/modules/foundation.md`.
- Kept the required `## Deletion strategy` heading as a short cross-reference
  and removed the duplicate deletion policy wording.
- Added the legacy SHA, read-only boundary, role boundary, image and issue #70
  boundary to `docs/runbooks/foundation-database.md`.
- Added the completion contract test and this evidence report.

## Verification

Final focused test:

```text
dotnet test tests/public/Vinx.Erp.ArchitectureTests --filter FullyQualifiedName~LegacyInventory
```

Result: passed, `1/1`.

Final complete public verification:

```text
./scripts/verify-public.sh
```

Result: exit code `0`. Web lint, format check, test, build and no-direct-
Supabase guard passed; .NET restore/build/format passed; Foundation database,
schema, model-drift and failed-migration guards passed; Architecture,
Foundation BDD, integration and unit public test projects passed.

The first complete-verification attempt exposed the pre-existing public
heading contract for `## Deletion strategy`; the documentation was corrected
with a non-duplicating cross-reference and the complete verifier was rerun to
green.

Commit is created only after the final `git diff --check` and explicit scope
checks pass, with message:

```text
docs: inventory legacy foundation candidates
```
