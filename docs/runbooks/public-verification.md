# Public verification

Run the builder-visible quality gates from the repository root:

```bash
./scripts/verify-public.sh
```

The runner installs the locked Node workspace, checks the React application,
prevents browser access to Supabase, restores and builds the .NET solution,
and runs public tests. It intentionally does not discover, run or report the
private quality suite.

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
