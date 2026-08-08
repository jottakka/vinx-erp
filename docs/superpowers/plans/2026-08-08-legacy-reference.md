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

Create `docs/legacy/README.md` with commands that clone the configured URL into a sibling directory, check out the pinned commit, make the checkout read-only for ordinary inspection, and never add it as a subdirectory or submodule of the new repository. Explain that the committed reference metadata contains no local checkout path that exposes user-specific secrets.

- [ ] **Step 2: Record the unavailable source state**

Create `docs/legacy/reference.md` with explicit fields:

```markdown
# Legacy Source Reference

| Field | Value |
|---|---|
| Status | unavailable — source URL and immutable commit not configured |
| Source URL | Not registered |
| Pinned commit or tag | Not registered |
| Inspection date | 2026-08-08 |
| Checkout location | Outside this repository; choose a local sibling directory |
```

Add a rule that no cited path becomes confirmed evidence until the URL and commit are filled with a real immutable value.

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

Add tables for screens, migrations/schema, services and workflows. Include the currently documented evidence without presenting it as locally verified:

| Category | Evidence reference | Current observation | Confidence | Decision |
|---|---|---|---|---|
| Frontend screens | Issue #2 and #12 | The legacy frontend is a React/Vite application with real and template screens; exact screen inventory awaits the pinned checkout. | provisional | adapt |
| Database migrations | Issue #43 and architecture docs | The old migration chain is not a reproducible baseline and contains cited dependency/DDL concerns. | provisional | adapt |
| Authorization policies | Issue #43 and issue #3 | Existing access behavior is insufficient for the target per-company capability model. | provisional | discard |
| RH workflows | Issues #8 and #51 | Existing RH persistence is not evidence of a safe medical-data workflow. | provisional | adapt |
| Service Desk workflows | Issue #7 | Existing ticket behavior is a reference only; domain commands and visibility need a new boundary. | provisional | adapt |
| Kanban workflows | Issue #6 | Existing work/project behavior is reference material; company scope and versioned transitions are required. | provisional | adapt |

Add a follow-up table for items requiring the pinned source, including exact paths, screenshots or tests, and owner/next review.

- [ ] **Step 3: Verify inventory traceability**

Run:

```bash
rg -n "provisional|preserve|adapt|discard|hypothesis|Issue #|Confidence|Decision" docs/legacy/inventory.md
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
git ls-files docs/legacy
git status --short
```

Expected: only the three Markdown files are tracked under `docs/legacy`; the worktree is clean after the commits.

- [ ] **Step 2: Check acceptance criteria**

Run:

```bash
git diff HEAD~2..HEAD --check
rg -n "Status|Source URL|Pinned commit|Inspection date|preserve|adapt|discard|provisional" docs/legacy
```

Expected: metadata, workflow and decision vocabulary are present with no whitespace errors.

- [ ] **Step 3: Report the handoff**

Report the worktree path, branch, commit SHAs, source-unavailable status, and the next required input: the legacy repository URL plus immutable commit/tag.
