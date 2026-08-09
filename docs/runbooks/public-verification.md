# Public verification

Run the builder-visible quality gates from the repository root:

```bash
./scripts/verify-public.sh
```

The runner installs the locked Node workspace, checks the React application,
prevents browser access to Supabase, restores and builds the .NET solution,
then verifies the Foundation migrations and committed schema snapshot before
running public tests. It intentionally does not discover, run or report the
private quality suite.

The migration verifier starts its own pinned, disposable PostgreSQL container.
It detects a model change without a Foundation migration, applies the migration
history and compares a normalized schema-only dump with the reviewed snapshot.
The negative EF guard also proves the model-pending check with a controlled
drift project and proves a failing `BrokenMigration` is absent from migration
history. Those checks run before later snapshot and public-test work.

Storage verification is a separate CI job and invokes only
`./scripts/verify-supabase-storage.sh`; it is not part of
`verify-public.sh`.
The storage verifier captures local API and service-role configuration without
printing either value, then unsets both values and stops Supabase on exit.
It honors an explicitly supplied `DOCKER_CONTEXT`; otherwise it prefers the
available Docker Desktop context on macOS and leaves Docker to use its default
context on Linux. A local startup failure reports only a generic Docker/context
diagnostic, never the Supabase CLI output.

To inspect the local Supabase runtime outside CI, use the Docker Desktop
context for that command only:

```bash
DOCKER_CONTEXT=desktop-linux npx supabase status
```

This repository can use GitHub Actions on GitHub Free, but branch protection
options for private repositories may be unavailable or insufficient for the
desired policy. Until the repository plan provides enforceable protection, no
agent may push or merge `main`; merges are a deliberate owner operation after
review and required public verification.
