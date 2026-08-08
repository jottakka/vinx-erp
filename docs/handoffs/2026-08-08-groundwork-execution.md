# Groundwork execution handoff

## Current state

- Architecture baseline: `docs/superpowers/specs/2026-08-08-vinx-erp-architecture-design.md`.
- Execution plan: `docs/superpowers/plans/2026-08-08-groundwork.md`.
- Current branch: `codex/groundwork-plan`; it is clean and contains documentation only.
- No application, database schema, authentication flow or business module has been scaffolded yet.

## Verified local tools

| Tool | Verified version/state |
|---|---|
| .NET SDK | 10.0.302 |
| Node.js | 22.23.2 |
| Docker Desktop | 29.6.2 daemon running |
| Docker Compose | 5.3.1 |
| GitHub CLI | 2.97.0 |
| Basic Memory | 0.22.1, local semantic index healthy |

Basic Memory is configured in Codex as a local MCP server constrained to the
`vinx-erp-docs` project. It indexes only `docs/`; its local configuration and
SQLite index at `~/.basic-memory` are disposable. It must never be treated as
the durable source of a decision. Restart Codex before expecting its MCP tools
in a new task.

Installed Codex skills for upcoming work are `aspnet-core`, `playwright` and
`security-best-practices`. They become available in a new task. The agent must
also use the applicable planning, worktree, TDD and verification skills.

## Start command

From the repository root, create the implementation worktree from this handoff
branch:

```bash
git worktree add ../vinx-erp-groundwork -b codex/groundwork codex/groundwork-plan
cd ../vinx-erp-groundwork
```

Read the architecture baseline and groundwork plan completely. Execute only
Task 1 first, including its public verification and focused commit. Stop for a
review checkpoint before Task 2.

## Agent separation

- Builder: performs the current plan task and may use `tests/public` only.
- Quality author: works separately, writes `quality/private`, and does not
  modify production code.
- Verifier: runs public and private gates in separate worktrees and reports
  redacted, actionable evidence only.

The private folder is an agreed process boundary, not a technical access
control. No actor may push, merge or force-update `main`.

## Ready-to-use task prompt

```text
Continue Vinx ERP from docs/handoffs/2026-08-08-groundwork-execution.md.
Read docs/superpowers/specs/2026-08-08-vinx-erp-architecture-design.md and
docs/superpowers/plans/2026-08-08-groundwork.md completely. Use the installed
ASP.NET Core, Playwright and security skills when their scope applies. Create
the prescribed isolated worktree and execute only Task 1 using TDD where code
is introduced. Do not access quality/private. Run the public checks, update
the required Markdown, commit only Task 1, then hand off the exact verification
results and wait for review before Task 2.
```
