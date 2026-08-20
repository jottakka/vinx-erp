# Paguai Decimal Amount Follow-up Implementation Plan

> **For agentic workers:** Execute this plan inline and preserve the existing RLS, capability, audit, idempotency, cache, and correlation contracts.

**Goal:** Replace the merged Paguai centavo integer contract with an exact two-decimal BRL amount while keeping creation, listing, and detail in `rascunho` only.

**Architecture:** Rename `amountMinor` to `amount` across the Foundation entity, EF mapping, strict module/HTTP contract, service hash/projection, API model, and React client. Persist `decimal` as PostgreSQL `numeric(18,2)` and reject values with more than two fractional digits before persistence so the database never silently rounds.

**Tech Stack:** .NET 10, ASP.NET Core controllers, EF Core/Npgsql, PostgreSQL, React/TypeScript, Zod, Vitest, public integration tests.

## Global Constraints

- Money is a positive `decimal` with exactly zero, one, or two fractional digits; no `double`, `float`, `long`, or `bigint` money representation.
- The database column is `numeric(18,2)` and the API JSON field is `amount`, a canonical decimal string with two fractional digits.
- Currency is exactly `BRL`; due date is `DateOnly`/`date` and JSON `YYYY-MM-DD`.
- Beneficiary is trimmed display-only text, max 200, and never enters logs, audit, metrics, outbox, or Markdown.
- Proof requirement is exactly `optional` or `required`, with explicit input and no default.
- Only create/list/detail are in scope; state remains `rascunho`; no notification, worker, outbox, or WhatsApp changes.

## File Map and Estimate

- Backend/API/persistence: 14 non-test files, approximately 180 manual lines; the conversion migration and generated designer/snapshot are included in the file count.
- Frontend: 2 files, approximately 70 manual lines.
- Tests: 5 files, approximately 100 manual lines.
- Durable documentation: 4 files, approximately 45 manual lines.
- Total: 25 files, including the generated migration designer and snapshot; approximately 395 manual lines across implementation, tests, and documentation.
- One cohesive vertical slice; the estimate is below the repository limits and does not justify splitting frontend and backend.

## Execution Tasks

### Task 1: Lock the decimal contract with failing tests

**Files:** `tests/public/Vinx.Erp.Foundation.IntegrationTests/PaguaiDraftEndpointTests.cs`, `tests/public/Vinx.Erp.Foundation.IntegrationTests/OpenApiInferenceContractPinTests.cs`, `apps/web/src/features/paguai/paguai-draft-client.test.ts`.

- [x] Change the success fixture and response assertions from `amountMinor: 125000` to `amount: "1250.00"`.
- [x] Add coverage for zero, negative, and more-than-two-decimal amounts.
- [x] Pin the OpenAPI property as `amount` with exact decimal-string semantics, a two-decimal pattern, and decimal minimum/maximum metadata.
- [x] Pin the browser request body to send `amount` as an exact JSON string, not `amountMinor`.
- [x] Run the focused tests and confirm they fail because production still exposes the old integer contract.

### Task 2: Implement the backend decimal contract

**Files:** `src/Foundation/Vinx.Erp.Foundation/Domain/PaguaiRequest.cs`, `src/Foundation/Vinx.Erp.Foundation/Persistence/Configurations/PaguaiRequestConfiguration.cs`, `src/Hosts/Vinx.Erp.ApiHost/Models/CreatePaguaiDraftRequest.cs`, `src/Modules/Vinx.Erp.Paguai/PaguaiDraftContracts.cs`, `src/Modules/Vinx.Erp.Paguai/PaguaiDraftHttpContract.cs`, `src/Modules/Vinx.Erp.Paguai/PaguaiDraftService.cs`, `src/Hosts/Vinx.Erp.ApiHost/Controllers/PaguaiController.cs`, `src/Hosts/Vinx.Erp.ApiHost/Constants/ApiConstants.cs`.

- [x] Replace `AmountMinor: long` with `Amount: decimal` and replace the JSON field with `amount`.
- [x] Validate `0.01m <= amount <= 9999999999999999.99m` and reject a scale greater than two without rounding.
- [x] Configure EF/Npgsql as `numeric(18,2)` and include a fixed-scale `amount` in the canonical idempotency hash and replay projection.
- [x] Make the strict parser use `TryGetDecimal` and apply the same scale/range rules.
- [x] Keep authorization, RLS, audit redaction, concurrency, and state behavior unchanged.

### Task 3: Update persistence artifacts

**Files:** `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260820061738_ConvertPaguaiAmountMinorToDecimal.cs`, generated designer, and `FoundationDbContextModelSnapshot.cs`.

- [x] Add a follow-up conversion migration that renames `amount_minor`, converts cents to reais in `numeric(18,2)`, updates stored receipt JSON, and preserves legacy hashes for replay; leave the historical cutover migration unchanged.
- [x] Regenerate the designer and model snapshot from the updated model.

### Task 4: Update the browser contract and presentation

**Files:** `apps/web/src/features/paguai/paguai-draft-client.ts`, `apps/web/src/features/paguai/paguai-page.tsx`.

- [x] Parse Brazilian/English decimal input without binary floating-point arithmetic and reject malformed grouping.
- [x] Send the canonical decimal as an exact JSON string and validate server responses with at most two fractional digits.
- [x] Display `amount` using BRL locale formatting and keep the existing accessible validation and draft flow.

### Task 5: Update durable evidence and verify

**Files:** `docs/modules/paguai.md`, `docs/contracts/openapi/README.md`, `docs/verification/issue-258-paguai-financial-draft-test-cases.md`, this plan.

- [x] Document `amount`/`decimal`/`numeric(18,2)` and remove the obsolete centavo transport limit.
- [x] Run focused backend/frontend tests, format/build checks, and `./scripts/verify-public.sh`.
- [x] Inspect the final diff for stale `amountMinor`, `long`, `bigint`, or binary floating-point money references outside historical material.
