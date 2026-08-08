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
