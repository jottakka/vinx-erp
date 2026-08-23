# Issue #173 Confirmation Replay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make concurrent confirmations with the same `RequestId` and payload converge on the committed `confirmed` result without duplicate inspection, scan, audit, or idempotency state.

**Architecture:** Keep the existing advisory lock and reconciliation helper. A deterministic EF Core command interceptor pauses the first request after its empty idempotency read, allows an identical request to commit, and then proves the first request reconciles the terminal file through `ReturnConcurrentConfirmationReplayAsync` instead of returning a premature `integrity_conflict`.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core interceptors, PostgreSQL/Testcontainers, xUnit.

## Global Constraints

- Start from current `origin/main`; do not reuse or cherry-pick the stale issue branch.
- Use strict RED → GREEN TDD and retain the failing/passing command evidence in the implementer report.
- Preserve membership, capability, tenant/RLS, malware, advisory-lock, audit, and idempotency guarantees.
- Same request and payload replays `200 confirmed`; divergent payload remains `409 idempotency_conflict`; distinct request IDs retain `409 integrity_conflict`.
- Add no migration, package, polling, delay, retry loop, stronger isolation level, or generic abstraction.
- Keep documentation in compact Portuguese and do not access `quality/private`.
- Count every manual test line; generated lines are zero. Freeze scope after the review correction round.

---

### Task 1: Deterministic read-skew regression and minimal reconciliation

**Files:**
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationApiFactory.cs`
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/DocumentUploadEndpointTests.cs`
- Modify: `src/Foundation/Vinx.Erp.Foundation/Files/DocumentUploadService.cs`
- Create: `docs/verification/issue-173-concurrent-confirmation-replay-test-cases.md`

**Interfaces:**
- Consumes: `DocumentUploadService.ConfirmAsync`, `ReturnConcurrentConfirmationReplayAsync`, the existing real PostgreSQL fixture, `ControlledFileStorage`, and `ControlledMalwareScanner`.
- Produces: test-only `FirstConfirmationIdempotencyReadGate` wiring and unchanged public HTTP contracts.

- [x] **Step 1: Add the deterministic timing gate and failing integration test**

  Add an optional `DbCommandInterceptor` hook to `FoundationApiFactory` using the existing test-service registration pattern. In `DocumentUploadEndpointTests`, implement `FirstConfirmationIdempotencyReadGate` so its first matching `ReaderExecutedAsync` call:

  1. matches a command containing `idempotency_records` and the parameter value `foundation.files.upload.confirm`;
  2. signals `FirstRead` only after the real empty query completes;
  3. awaits `Release` with a bounded cancellation token;
  4. always permits release from test cleanup.

  Add `Concurrent_same_request_replays_when_idempotency_read_precedes_the_winner_commit`: start request A, await `FirstRead`, complete identical request B as `200 confirmed`, release A, then assert both bodies contain the same file and `confirmed`; exactly one inspection, malware scan, success audit, and idempotency record exist; no denied `integrity_conflict` audit exists.

- [x] **Step 2: Run the new test and capture the expected RED**

  Run:

  ```bash
  dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~Concurrent_same_request_replays_when_idempotency_read_precedes_the_winner_commit -v minimal
  ```

  Expected: request B is `200 confirmed`, request A is `409 integrity_conflict`, and the assertion requiring two successful replays fails. A timeout, setup error, or synthetic exception is not an acceptable RED.

- [x] **Step 3: Implement the minimal production fix**

  In `PrepareConfirmationAsync`, replace only the terminal-file direct denial with the existing reconciliation path:

  ```csharp
  if (!string.Equals(processingState, FileRecord.QuarantinedState, StringComparison.Ordinal))
  {
      return ConfirmationPreparation.Completed(
          await ReturnConcurrentConfirmationReplayAsync(
              actor,
              command,
              payloadHash,
              cancellationToken));
  }
  ```

  Do not change finalization exception handling, locking, transaction isolation, or PostgreSQL error classification.

- [x] **Step 4: Run the deterministic test and capture GREEN**

  Re-run the exact Step 2 command. Expected: `1/1` passes with pristine output and one-time durable effects.

- [x] **Step 5: Add the realistic divergent-payload regression**

  Add `Confirmation_with_same_request_id_and_different_file_returns_idempotency_conflict`: confirm file 1, then confirm file 2 in the same company with the same `RequestId`; assert `409 idempotency_conflict`, file 2 remains quarantined, and the original one-time effects are not duplicated.

- [x] **Step 6: Run focused regression coverage**

  Run the two new tests plus these existing methods using one filter expression:

  - `Concurrent_confirmation_with_the_same_request_creates_one_confirmed_result`
  - `Concurrent_confirmations_with_distinct_request_ids_inspect_once_and_replay_their_results`
  - `Repeated_confirmation_replays_confirmed_state_without_a_second_audit_or_exposure`
  - `Confirmation_reauthorizes_after_private_inspection_finishes`

  Expected: all selected tests pass; no output warnings or errors.

- [x] **Step 7: Add the verification manifest**

  Create a compact Portuguese table mapping success, validation/error, access, state/exception, and concurrency BDD classes to exact test methods. Mark existing unchanged authorization tests as covered by name; do not copy logs.

- [x] **Step 8: Verify scope and commit**

  Run `git diff --check`, recalculate counted files/manual lines, and confirm zero migration/snapshot/lockfile changes. Commit the issue slice with a concise conventional subject. The implementer report must include RED, GREEN, focused-test output, changed files, and the size estimate. Do not run the full public gate inside this task; the controller runs it once after independent review and corrections.
