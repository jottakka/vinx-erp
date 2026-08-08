# Legacy Reference and Inventory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a versioned, read-only legacy-reference workflow and an evidence-based initial inventory for issue #12.

**Architecture:** Keep the legacy checkout outside the repository and commit only Markdown metadata and inventory. Mark the source as unavailable until an immutable URL and commit are supplied; treat issue and architecture references as provisional evidence.

**Tech Stack:** Git worktree and Markdown documentation.

## Global Constraints

- Do not copy legacy source, migrations, dependencies, data or secrets into this repository.
- Do not claim legacy paths are locally verified until a source URL and immutable commit are registered.
- Use `preserve`, `adapt`, `discard` and `hypothesis` as the inventory decisions.
- Keep the legacy checkout physically separate and read-only.

---

### Task 1: Document the legacy reference workflow

**Files:**
- Create: `docs/legacy/README.md`
- Create: `docs/legacy/reference.md`

**Interfaces:**
- `docs/legacy/README.md` explains how a developer obtains and inspects the source.
- `docs/legacy/reference.md` records source URL, immutable commit, inspection date, checkout location and status.

- [ ] **Step 1: Write the read-only workflow**

Create `docs/legacy/README.md` with `set -eu` commands that use `mktemp -d`
without a caller-provided root or template, clone into its `checkout` child,
detach at the configured full immutable SHA, verify
`git -C "$LEGACY_DIR" rev-parse HEAD` exactly equals `LEGACY_COMMIT`, then make
the checkout read-only for ordinary inspection. The workflow must fail on clone,
checkout, or hash mismatch and must never add the checkout as a subdirectory or
submodule of the new repository. Explain that committed reference metadata
contains no local checkout path that exposes user-specific secrets.

- [ ] **Step 2: Record the unavailable source state**

Create `docs/legacy/reference.md` with explicit fields:

```markdown
# Legacy Source Reference

| Field | Value |
|---|---|
| Status | unavailable — source URL and immutable commit not configured |
| Source URL | Not registered |
| Pinned immutable commit SHA | Not registered |
| Inspection date | Not inspected |
| Checkout location | System temporary directory outside this repository and its worktrees |
```

Add a rule that no cited path becomes confirmed evidence until the URL and full
immutable commit SHA are filled with real values; a branch or tag is not an
acceptable substitute.

- [ ] **Step 3: Verify the documentation contract**

Run:

```bash
rg -n "read-only|immutable|commit|outside|secret|not configured" docs/legacy/README.md docs/legacy/reference.md
git diff --check
```

Expected: all required concepts are present and `git diff --check` exits 0.

- [ ] **Step 4: Commit the workflow**

```bash
git add docs/legacy/README.md docs/legacy/reference.md
git commit -m "docs: record legacy reference workflow"
```

### Task 2: Create the evidence-based inventory

**Files:**
- Create: `docs/legacy/inventory.md`

**Interfaces:**
- The inventory links each entry to a source document or issue, identifies confidence, and assigns exactly one decision.

- [ ] **Step 1: Add inventory rules and decision vocabulary**

Document that `confirmed` requires inspection of the pinned checkout, while `provisional` may rely on current issue/architecture evidence. Define `preserve`, `adapt`, `discard` and `hypothesis` in one table.

- [ ] **Step 2: Record known categories and evidence**

Add one canonical artifact-level table with the columns `Area`, `Provisional
reported path or artifact identifier`, `Linked evidence`, `Observed behavior`,
`Confidence`, and `Disposition`. Use only issue-supported, explicitly
provisional rows, each with `hypothesis` as its one disposition:

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

Add a verification-backlog table that links each follow-up to its issue and
requires source inspection before recording paths, behavior, or table names as
confirmed.

- [ ] **Step 3: Verify inventory traceability**

Run:

```bash
set -eu
rg -n "provisional|preserve|adapt|discard|hypothesis|Issue #|Confidence|Disposition" docs/legacy/inventory.md
git diff --check
```

Expected: every initial entry has evidence, confidence and a decision; no entry claims local confirmation.

- [ ] **Step 4: Commit the inventory**

```bash
git add docs/legacy/inventory.md
git commit -m "docs: add provisional legacy inventory"
```

### Task 3: Run the final acceptance checks

**Files:**
- Verify: `docs/legacy/README.md`
- Verify: `docs/legacy/reference.md`
- Verify: `docs/legacy/inventory.md`

- [ ] **Step 1: Check for prohibited source material**

Run:

```bash
set -eu

BRANCH_BASE="$(git merge-base HEAD origin/main 2>/dev/null || git merge-base HEAD main 2>/dev/null || git rev-list --max-parents=0 HEAD)"
git diff --name-status "$BRANCH_BASE"..HEAD
git diff --name-only "$BRANCH_BASE"..HEAD
if git diff --name-only "$BRANCH_BASE"..HEAD | rg -n -i '(^|/)(src|supabase|node_modules|vendor|dist|build|data|secrets?|credentials?)/|(^|/)\.env($|\.)|\.(c|cc|cpp|cs|go|java|js|jsx|ts|tsx|py|rb|php|rs|sh|sql|csv|tsv|sqlite|db|parquet|zip|tar|gz)$'; then
  echo "Prohibited legacy source or material found in the branch diff" >&2
  exit 1
fi
```

Expected: the complete branch diff lists only documentation and repository
metadata; it contains no legacy source, dependencies, data, secrets, or other
prohibited material.

- [ ] **Step 2: Check acceptance criteria**

Run:

```bash
set -eu

BRANCH_BASE="$(git merge-base HEAD origin/main 2>/dev/null || git merge-base HEAD main 2>/dev/null || git rev-list --max-parents=0 HEAD)"
git diff --check "$BRANCH_BASE"..HEAD
git diff --name-status "$BRANCH_BASE"..HEAD
git diff --name-only "$BRANCH_BASE"..HEAD
for metadata in \
  '| Status | unavailable — source URL and immutable commit not configured |' \
  '| Source URL | Not registered |' \
  '| Pinned immutable commit SHA | Not registered |' \
  '| Inspection date | Not inspected |' \
  '| Checkout location | System temporary directory outside this repository and its worktrees |'; do
  rg -Fqx "$metadata" docs/legacy/reference.md
done
awk '
  function trim(value) {
    sub(/^[[:space:]]+/, "", value)
    sub(/[[:space:]]+$/, "", value)
    return value
  }
  function markdown_value(value) {
    value = trim(value)
    sub(/^`/, "", value)
    sub(/`$/, "", value)
    return value
  }
  /^## Canonical artifact inventory$/ { canonical = 1; next }
  /^## Verification backlog$/ { canonical = 0 }
  canonical && /^\|/ {
    count = split($0, column, "|")
    if (!header_seen) {
      for (field_index = 2; field_index < count; field_index++) {
        heading = trim(column[field_index])
        if (heading == "Provisional reported path or artifact identifier") artifact_column = field_index
        if (heading == "Linked evidence") evidence_column = field_index
        if (heading == "Disposition") disposition_column = field_index
      }
      if (!artifact_column || !evidence_column || !disposition_column) {
        print "Canonical inventory headers are incomplete" > "/dev/stderr"
        invalid = 1
      }
      header_seen = 1
      next
    }
    if (trim(column[2]) ~ /^:?-+:?$/) next
    artifact = trim(column[artifact_column])
    evidence = trim(column[evidence_column])
    disposition = markdown_value(column[disposition_column])
    rows++
    if (artifact == "") {
      print "Missing artifact identifier" > "/dev/stderr"
      invalid = 1
    }
    if (evidence !~ /\[[^]]+\]\([^)]+\)/) {
      print "Missing linked evidence: " artifact > "/dev/stderr"
      invalid = 1
    }
    if (disposition !~ /^(preserve|adapt|discard|hypothesis)$/) {
      print "Invalid or non-single disposition: " artifact > "/dev/stderr"
      invalid = 1
    }
  }
  END {
    if (!header_seen) {
      print "No canonical inventory header found" > "/dev/stderr"
      invalid = 1
    }
    if (!rows) {
      print "No canonical inventory rows found" > "/dev/stderr"
      invalid = 1
    }
    exit invalid
  }
' docs/legacy/inventory.md
```

Expected: the full branch range has no whitespace errors; required reference
metadata is present; and every canonical inventory row has linked evidence and
exactly one valid disposition.

- [ ] **Step 3: Report the handoff**

Report the worktree path, branch, commit SHAs, source-unavailable status, and the next required input: the legacy repository URL plus full immutable commit SHA.
