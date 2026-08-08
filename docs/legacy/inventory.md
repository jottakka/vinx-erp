# Legacy Evidence Inventory

This is the initial, evidence-based inventory for the legacy application. The
legacy source is currently unavailable: `reference.md` has no registered source
URL or immutable commit. The entries below therefore use current issue and
architecture documents as provisional evidence only. No legacy path, screen,
workflow, migration, or policy is locally confirmed.

## Inventory rules

`confirmed` requires direct inspection of the pinned, read-only checkout. A
`provisional` entry may rely on current issue or architecture evidence, but it
must be revisited after the source URL and immutable commit are registered.

Every entry has exactly one decision. The decision vocabulary is:

| Decision | Meaning |
|---|---|
| `preserve` | Retain the observed behavior or artifact, subject to validation against the target architecture. |
| `adapt` | Use the evidence as input, but change the behavior or artifact to meet the target architecture and requirements. |
| `discard` | Do not carry the observed behavior or artifact into the target implementation. |
| `hypothesis` | Record an item that still needs evidence before a preserve, adapt, or discard decision is justified. |

## Known categories

### Screens and schema

| Category | Evidence reference | Current observation | Confidence | Decision |
|---|---|---|---|---|
| Frontend screens | Issue #2 and #12 | The legacy frontend is a React/Vite application with real and template screens; exact screen inventory awaits the pinned checkout. | `provisional` | `adapt` |
| Database migrations | Issue #43 and architecture docs | The old migration chain is not a reproducible baseline and contains cited dependency/DDL concerns. | `provisional` | `adapt` |

### Services and policies

| Category | Evidence reference | Current observation | Confidence | Decision |
|---|---|---|---|---|
| Authorization policies | Issue #43 and issue #3 | Existing access behavior is insufficient for the target per-company capability model. | `provisional` | `discard` |

### Workflows

| Category | Evidence reference | Current observation | Confidence | Decision |
|---|---|---|---|---|
| RH workflows | Issues #8 and #51 | Existing RH persistence is not evidence of a safe medical-data workflow. | `provisional` | `adapt` |
| Service Desk workflows | Issue #7 | Existing ticket behavior is a reference only; domain commands and visibility need a new boundary. | `provisional` | `adapt` |
| Kanban workflows | Issue #6 | Existing work/project behavior is reference material; company scope and versioned transitions are required. | `provisional` | `adapt` |

## Follow-up requiring the pinned source

The exact legacy paths below are intentionally unavailable until the source URL
and immutable commit are registered. The requested screenshots and tests are
the evidence to collect from that checkout; their absence is not evidence that
the corresponding artifact does not exist.

| Item | Exact path in pinned checkout | Screenshot or test to collect | Owner | Next review | Confidence | Decision |
|---|---|---|---|---|---|---|
| Frontend screen inventory | Not available until the pinned checkout is registered | Route index plus screenshots of real and template screens; route smoke test | Legacy-reference maintainer | After checkout registration | `hypothesis` | `hypothesis` |
| Migration chain and schema | Not available until the pinned checkout is registered | Ordered migration listing, dependency/DDL review, and clean replay test | Data/platform owner | After checkout registration | `hypothesis` | `hypothesis` |
| Authorization behavior | Not available until the pinned checkout is registered | Policy/middleware paths, representative access matrix, and authorization tests | Foundation/security owner | After checkout registration | `hypothesis` | `hypothesis` |
| RH workflows | Not available until the pinned checkout is registered | RH route/component paths, data-flow diagram, and medical-data boundary tests | HR module owner | After checkout registration | `hypothesis` | `hypothesis` |
| Service Desk workflows | Not available until the pinned checkout is registered | Ticket route/service paths, visibility screenshots, and command/transition tests | Service Desk owner | After checkout registration | `hypothesis` | `hypothesis` |
| Kanban workflows | Not available until the pinned checkout is registered | Board/project paths, company-scope screenshots, and versioned-transition tests | Kanban owner | After checkout registration | `hypothesis` | `hypothesis` |

## Review gate

When `reference.md` contains a real source URL and immutable commit, inspect a
fresh read-only checkout outside this repository. Replace each unavailable
path with the exact inspected path, attach the collected screenshot or test
evidence, update confidence, and retain exactly one decision per entry.
