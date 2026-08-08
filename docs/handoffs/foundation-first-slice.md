# Foundation first slice handoff

The first implementation slice establishes authorized membership read access
and the API capability map for one active company context. It makes the
existing public BDD scenario executable, records allow and deny audit events,
and propagates the OpenTelemetry correlation identifier.

## Builder

Implement membership read access and the API capability map. Keep company
context explicit, use the Foundation boundary, and do not read
`quality/private`.

## Quality author

Write the private abuse coverage for a forged or missing company context and
cross-company cache leakage. Do not change production code. The currently
empty private project is custody infrastructure, not coverage; these abuse
tests are part of this first slice.

## Verifier

Run public and private gates in separate worktrees. Report only the redacted,
actionable failure evidence a builder needs to fix the public implementation.

## Excluded from this slice

Login, payment approval, file upload and MCP write actions are deliberately
out of scope.
