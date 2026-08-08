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

The legacy authorization evidence is classified around `anon`,
`authenticated` and `service_role`; it is reference evidence, not provider
state. Any future isolated database check uses `postgres:17.9-alpine` and a
schema-only snapshot boundary:

```bash
pg_dump --schema-only --no-owner --no-privileges \
  --schema=foundation --schema=audit
```

The snapshot must contain no data and must not be used to create the schema.
Production preflight, provider state and deployment remain outside this task
and belong to issue #70.
