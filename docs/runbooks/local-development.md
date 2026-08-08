# Local development

## Prerequisites

Install Node.js, .NET and Docker Desktop at the versions recorded in the
repository handoff. The Supabase CLI is a project dependency; do not install it
globally.

## Start local services

```bash
npm install
npx supabase start
npx supabase status
```

With the default local configuration, the main service URLs are:

- API: `http://127.0.0.1:54321`
- Studio: `http://127.0.0.1:54323`
- Mailpit: `http://127.0.0.1:54324`

The local PostgreSQL port is `54322`; other generated credentials and service
URLs are displayed by `npx supabase status`. Keep them local. `.env.local` is
ignored by Git and must never be committed.

The repository's Compose file declares only the shared `vinx-erp` network. It
does not start a second PostgreSQL, Auth or Storage service alongside
Supabase.

## Troubleshooting startup

The first `npx supabase start` downloads the local Docker images and can take
some time. If a transient health-check timeout occurs, wait for the services
to stabilize, then check them with `npx supabase status`. Use
`--ignore-health-check` only for diagnosis, never as the normal start command.

Vinx ERP assumes Docker Desktop. If Docker Desktop and another runtime such as
Colima coexist, first confirm the intended runtime with `docker context show`.
Keep the user's global context unchanged and select Docker Desktop only for a
Vinx command when needed:

```bash
DOCKER_CONTEXT=desktop-linux npx supabase start
DOCKER_CONTEXT=desktop-linux npx supabase status
```

## Stop local services

```bash
npx supabase stop
```

Use `npx supabase stop --no-backup` only when discarding local database state
is intentional.
