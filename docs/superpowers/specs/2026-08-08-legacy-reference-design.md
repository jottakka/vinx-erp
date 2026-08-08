# Legacy Reference and Inventory Design

## Status

Approved design for issue #12. The implementation records a reproducible
reference contract and an evidence-based initial inventory without copying the
legacy application into the new repository.

## Goals

- Make the legacy system consultable without relying on memory.
- Record the exact source URL and commit before treating legacy behavior as
  confirmed.
- Keep the legacy source read-only and physically separate from the new code.
- Map known legacy artifacts to `preserve`, `adapt`, `discard`, or
  `hypothesis` decisions.

## Non-goals

- Importing legacy source, migrations, dependencies, or data into this repo.
- Executing legacy code as part of the new application's build.
- Deciding product behavior when the legacy evidence is unavailable.

## Repository artifacts

- `docs/legacy/README.md`: read-only checkout instructions, evidence rules and
  update procedure.
- `docs/legacy/reference.md`: source URL, pinned commit, inspection date and
  reference status. It must explicitly say when the source has not yet been
  configured.
- `docs/legacy/inventory.md`: categorized inventory with source path,
  observed behavior, confidence and preserve/adapt/discard/hypothesis decision.

The first version will use the repository's existing issue and architecture
documents as provisional evidence. Paths cited by those documents will not be
claimed as locally verified until the external source URL and commit are
registered.

## Update workflow

1. Obtain the legacy repository URL and an immutable commit or tag.
2. Clone it outside the new repository, or fetch a clean read-only checkout.
3. Record URL, commit, date and checkout path in `reference.md` locally; only
   the URL, commit and evidence metadata are committed.
4. Inspect files and update `inventory.md` with exact paths and confidence.
5. Review every proposed copy or adaptation against the new architecture
   before implementation.

## Acceptance criteria

- A developer can follow `docs/legacy/README.md` to obtain the pinned source.
- `reference.md` distinguishes configured, provisional and unavailable states.
- `inventory.md` contains traceable categories and does not present
  unverified paths as local facts.
- No legacy source or secrets are committed to the new repository.
