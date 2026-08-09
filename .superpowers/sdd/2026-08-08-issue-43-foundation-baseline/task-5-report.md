# Task 5 report: Foundation migration and snapshot gates

## Scope

Implemented only Task 5: independent Foundation database verification,
explicit snapshot generation, real EF negative guards, public-gate ordering,
CI job separation and related public runbooks.

## TDD evidence

The first run of `./scripts/verify-foundation-database.sh` intentionally exited
nonzero with a unified diff. The committed artifact was a one-line deliberately
incorrect comment; the generated normalized dump began with PostgreSQL session
settings and the Foundation and audit schema. No connection string or password
was emitted. The canonical snapshot was then generated with
`./scripts/generate-foundation-schema-snapshot.sh --write`, after which the
database verifier passed.

## Verification evidence

- `./scripts/generate-foundation-schema-snapshot.sh` generated only a temporary
  artifact and left the committed snapshot unchanged.
- `./scripts/verify-foundation-database.sh` passed after applying the migration
  history in its own disposable `postgres:17.9-alpine` container.
- `./scripts/verify-ef-negative-guards.sh` passed. It generated an EF snapshot
  with only `DriftEntity.Id`, added `DriftEntity.Name` to the active model, and
  confirmed the model check failed. It then ran a generated `BrokenMigration`
  containing `SELECT 1 / 0`, confirmed database update failed with that
  identifier, and confirmed the migration was absent from
  `__EFMigrationsHistory`.
- `./scripts/verify-public.sh` completed its ordered checks. Its public project
  loop reported 3 architecture, 2 BDD and 38 integration tests passing; the
  unit-test project currently has no discovered tests.
- `git diff --check` passed.

## Compatibility note

EF Core 10.0.10 reports controlled drift as `Changes have been made to the
model since the last migration` rather than the older `pending changes`
wording. The guard requires a nonzero exit and accepts either wording while
still exercising the actual EF command and model difference.

## CI boundary

`verify-public` invokes only `scripts/verify-public.sh`. The separate
`verify-storage` job invokes only `scripts/verify-supabase-storage.sh`; the
latter script is supplied by Task 6 and is intentionally not implemented here.
