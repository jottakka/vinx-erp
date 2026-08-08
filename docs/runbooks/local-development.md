# Local development

## Prerequisites

Install Node.js, .NET and Docker Desktop at the versions recorded in the
repository handoff. The Supabase CLI is a project dependency; do not install it
globally.

## Start local services

```bash
npm install
DOCKER_CONTEXT=desktop-linux npx supabase start
DOCKER_CONTEXT=desktop-linux npx supabase status
```

With the default local configuration, the main service URLs are:

- API: `http://127.0.0.1:54321`
- Studio: `http://127.0.0.1:54323`
- Mailpit: `http://127.0.0.1:54324`

The local PostgreSQL port is `54322`; other generated credentials and service
URLs are displayed by `DOCKER_CONTEXT=desktop-linux npx supabase status`.
Keep them local. `.env.local` is ignored by Git and must never be committed.

The repository's Compose file declares only the shared `vinx-erp` network. It
does not start a second PostgreSQL, Auth or Storage service alongside
Supabase.

## Verify Foundation migrations and schema

The Foundation migration checks use a fresh `postgres:17.9-alpine` container
and do not use the local Supabase database or require a runtime database
credential:

```bash
./scripts/verify-foundation-database.sh
./scripts/verify-ef-negative-guards.sh
```

To deliberately refresh the reviewed schema artifact after a migration change,
run the explicit write command and inspect its diff:

```bash
./scripts/generate-foundation-schema-snapshot.sh --write
git diff -- docs/database/foundation-schema.snapshot.sql
```

## Troubleshooting startup

The first `npx supabase start` downloads the local Docker images and can take
some time. If a transient health-check timeout occurs, wait for the services
to stabilize, then check them with
`DOCKER_CONTEXT=desktop-linux npx supabase status`. Use
`--ignore-health-check` only for diagnosis, never as the normal start command.

Vinx ERP assumes Docker Desktop. If Docker Desktop and another runtime such as
Colima coexist, first confirm the intended runtime with `docker context show`.
Every Supabase command in this runbook explicitly selects Docker Desktop for
that process; it does not change the user's global Docker context.

## Stop local services

```bash
DOCKER_CONTEXT=desktop-linux npx supabase stop
```

Use `DOCKER_CONTEXT=desktop-linux npx supabase stop --no-backup` only when
discarding local database state is intentional.
