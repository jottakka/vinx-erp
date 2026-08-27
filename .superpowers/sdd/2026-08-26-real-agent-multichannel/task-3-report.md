# Task 3 report — completed

## Implemented working slice

- Added the shared `AgentExecutionService`, command/result contracts and a
  ledger lifecycle bridge in the AI module.
- Moved `POST /agent/runs` to a thin HTTP adapter; it no longer writes the
  post-provider `IdempotencyRecord` path.
- Added the required domain invariant: deterministic pre-dispatch outcomes can
  complete from `Claimed`; `VisionInFlight` still cannot complete until the
  main model dispatch.
- Persisted the ledger state immediately before vision and main-model calls.
  Provider timeout/cancellation/unknown outcomes are marked `Ambiguous`.
- Added a test-only, credentialless fake-model composition seam. Production
  credential resolution remains unchanged.

## Debug evidence

1. Initial endpoint replay returned 503. Detailed host logs showed PostgreSQL
   `42501`: the ledger insert violated RLS because `ApplyContextAsync` was
   outside the transaction that saved the initial claim.
2. Claim persistence was moved into `BeginTransaction -> ApplyContext -> Save
   -> Commit`. The next detailed replay found the second boundary: the
   `ModelInFlight` update affected zero rows under RLS and surfaced as a
   `DbUpdateConcurrencyException`.
3. Each lifecycle transition now uses that same RLS transaction pattern. The
   focused `Same_requestId_replays_same_body_without_a_second_call` endpoint
   regression then passed.

## Verification evidence

- `dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~AgentRunner|FullyQualifiedName~AgentExecution'`
  - passed: 37/37.
- `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~AgentOutcomeMappingTests|FullyQualifiedName~AgentRunCompositionTests|FullyQualifiedName~AgentExecutionLedgerTests'`
  - passed: 44/44 before the final endpoint-only metadata adjustment.
- Docker ownership preflight found no running Docker containers; it was clean
  to run the endpoint test fixture. No provider/network calls were made.
- The full `AgentEndpointTests` class was invoked after the focused replay
  passed, but the desktop command wrapper detached at 30 seconds and did not
  return its terminal outcome. Do not treat the full class as green.

## Final correction round

- Retained the existing completed-replay, changed-payload conflict, concurrent
  claim, deterministic completion, VisionInFlight prohibition and lifecycle
  transition regressions. Added a two-case stale VisionInFlight/ModelInFlight
  replay regression that proves the service marks the record ambiguous with no
  model retry.
- Restored the prior global audit for deterministic no-company outcomes without
  creating a global ledger claim.
- Kept timeout ledger state ambiguous while preserving the known 504
  `agent_timeout` HTTP contract; opaque confirmation results now persist and
  replay without the proposed action name.
- Restricted the credentialless test seam to the fixture: it uses an inert
  vision client while retaining the real authorized document source. Production
  vision credential resolution is unchanged.
- Restored audit metadata status values to the existing HTTP vocabulary.

## Final verification evidence

- Focused repaired endpoint regressions: 5/5 passed.
- Complete `AgentEndpointTests` class: 19/19 passed in an attached terminal
  session after a clean Docker ownership preflight.
- AI execution focus (`AgentRunner|AgentExecution`): 37/37 passed.
- Foundation outcome/composition/ledger focus: 44/44 passed.
