# Paguai Financial Draft Implementation Plan

> **For agentic workers:** Execute this plan inline with the approved product decisions and preserve the existing RLS, capability, audit, idempotency, cache, and correlation contracts.

**Goal:** Add the minimum financial fields to Paguai drafts while keeping creation, listing, and detail in `rascunho` only.

**Architecture:** Extend the Foundation entity and EF mapping, strict module/HTTP contracts, service hash and projection, controller/request validation, and the existing React client/page. A Foundation migration clears old Paguai draft/idempotency data before adding required columns and the BRL check constraint.

**Tech Stack:** .NET 10, ASP.NET Core controllers, EF Core/Npgsql, PostgreSQL, React/TypeScript, Zod, Vitest, public integration tests.

## Global Constraints

- Money is positive integer centavos (`long`/`bigint`); no `double` or `float`.
- Currency is exactly `BRL`; due date is `DateOnly`/`date` and JSON `YYYY-MM-DD`.
- Beneficiary is trimmed display-only text, max 200, and never enters logs, audit, metrics, outbox, or Markdown.
- Proof requirement is exactly `optional` or `required`, with explicit input and no default.
- Only create/list/detail are in scope; state remains `rascunho`; no notification, worker, outbox, or WhatsApp changes.

### Execution tasks

1. Extend `PaguaiRequest`, `CreateDraftCommand`, `PaguaiDraft`, and strict JSON parsing.
2. Extend `CreatePaguaiDraftRequest`, `PaguaiDraftService`, and `PaguaiRequestConfiguration`.
3. Add the migration that purges Paguai request/idempotency rows, adds required columns, and adds the BRL check.
4. Update controller response annotations and beneficiary trimming validation.
5. Update public integration/schema tests and BDD manifest.
6. Update the typed web client and page with centavo conversion and display formatting.
7. Update durable Paguai/OpenAPI documentation and run the public verification gate.
