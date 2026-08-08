# ADR 0002: Restrict provider-specific integrations to explicit boundaries

## Status

Accepted

## Context

Supabase is the initial managed PostgreSQL, identity and private file-storage provider. The ERP must remain able to change identity, storage, messaging or AI vendors without exposing a vendor API throughout its modules.

## Decision

The browser accesses ERP data and administration only through the ASP.NET Core API. Provider-specific implementations sit behind narrow, present-use-case boundaries: `IdentityProvider`, `FileStorage`, `WhatsAppGateway`, `EmailGateway`, `SmsGateway`, and AI protocol adapters selected by an approved task profile.

Use EF Core and Npgsql directly for normal PostgreSQL persistence; do not add a generic persistence abstraction. Supabase Auth and Storage adapters are server-side only. Files use private buckets and short-lived signed URLs.

## Consequences

- Switching a provider changes an adapter and its contract tests instead of business modules and frontend code.
- Supabase service-role credentials never reach the browser.
- PostgreSQL safeguards, including RLS and high-risk database commands, remain available instead of being hidden behind a lowest-common-denominator repository.
- Each adapter requires configuration, health signals, redacted diagnostics and integration coverage against its supported provider.
