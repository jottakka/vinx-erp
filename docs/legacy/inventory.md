# Legacy Evidence Inventory

The legacy source is unavailable: `reference.md` has no registered source URL
or pinned immutable commit SHA. This is therefore a canonical inventory of
artifact-level *reported identifiers*, not a record of locally verified source
files. Every identifier, wildcard, and table reference below requires source
verification from a detached checkout at the pinned SHA.

## Inventory rules

`confirmed` requires direct inspection of the pinned, read-only checkout.
`provisional` means that the linked issue reports an identifier, but neither its
existence in the source nor its behavior has been locally inspected.

Every canonical row has exactly one disposition:

| Disposition | Meaning |
|---|---|
| `preserve` | Retain the observed artifact or behavior, subject to target-architecture validation. |
| `adapt` | Use the observed artifact or behavior as input, with changes for the target architecture. |
| `discard` | Do not carry the observed artifact or behavior into the target implementation. |
| `hypothesis` | Keep the reported identifier pending source inspection; no preserve, adapt, or discard decision is yet justified. |

## Canonical artifact inventory

| Area | Provisional reported path or artifact identifier | Linked evidence | Observed behavior | Confidence | Disposition |
|---|---|---|---|---|---|
| Screen | `src/feature-module/Pages/vinx/workcenter/tickets.tsx` | [Issue #68](https://github.com/jottakka/vinx-erp/issues/68) | Reported screen path; screen behavior has not been inspected. | `provisional` | `hypothesis` |
| Migration/schema | `supabase/rbac_schema.sql` | [Issue #68](https://github.com/jottakka/vinx-erp/issues/68) | Reported schema artifact; schema behavior has not been inspected. | `provisional` | `hypothesis` |
| Migration/schema | Reported migration-0045 series identifier — filename not confirmed; source verification required | [Issue #43](https://github.com/jottakka/vinx-erp/issues/43) | Issue-reported migration-series identifier; its files and behavior have not been inspected. | `provisional` | `hypothesis` |
| Migration/schema | `supabase/migrations/0047_rh_ondas_bcde.sql` | [Issue #43](https://github.com/jottakka/vinx-erp/issues/43) | Reported migration artifact; migration behavior has not been inspected. | `provisional` | `hypothesis` |
| Migration/schema | Reported migration-0055 identifier — filename not confirmed; source verification required | [Issue #6](https://github.com/jottakka/vinx-erp/issues/6) | Issue-reported migration identifier; its filename and behavior have not been inspected. | `provisional` | `hypothesis` |
| Service | `ticketsService.ts` | [Issue #7](https://github.com/jottakka/vinx-erp/issues/7) | Reported service identifier; service behavior has not been inspected. | `provisional` | `hypothesis` |
| Service | `demandasService.ts` | [Issue #6](https://github.com/jottakka/vinx-erp/issues/6) | Reported service identifier; service behavior has not been inspected. | `provisional` | `hypothesis` |
| Policy | Reported RH 21-table policy span — table names unavailable; source verification required | [Issue #8](https://github.com/jottakka/vinx-erp/issues/8) | Issue-reported policy span; table names and policy behavior have not been inspected. | `provisional` | `hypothesis` |
| Workflow | `rh_ausencias/documento_url` — reported identifier requiring source verification | [Issue #8](https://github.com/jottakka/vinx-erp/issues/8) | Reported RH workflow/data identifier; workflow behavior has not been inspected. | `provisional` | `hypothesis` |

## Verification backlog

This backlog directs future inspection. It intentionally has no disposition
column; the canonical inventory above remains the only decision record.

| Verification target | Linked evidence | Source verification required |
|---|---|---|
| Reported screen identifier | [Issue #68](https://github.com/jottakka/vinx-erp/issues/68) | Confirm the exact path, route, and observed screen behavior. |
| Reported schema and migration identifiers | [Issue #68](https://github.com/jottakka/vinx-erp/issues/68), [Issue #43](https://github.com/jottakka/vinx-erp/issues/43), and [Issue #6](https://github.com/jottakka/vinx-erp/issues/6) | List matching files, inspect order and contents, and record observed behavior. |
| Reported service identifiers | [Issue #7](https://github.com/jottakka/vinx-erp/issues/7) and [Issue #6](https://github.com/jottakka/vinx-erp/issues/6) | Confirm the service locations and record their observed inputs, outputs, and callers. |
| Reported RH policy and workflow identifiers | [Issue #8](https://github.com/jottakka/vinx-erp/issues/8) | Identify the policy-span table names, then confirm its policies and the `rh_ausencias/documento_url` identifier; record observed behavior. |

## Review gate

When `reference.md` contains a real source URL and full immutable commit SHA,
inspect a fresh read-only checkout outside this repository. Replace reported
identifiers only with source-verified evidence, update confidence, and retain
exactly one disposition for each canonical artifact.
