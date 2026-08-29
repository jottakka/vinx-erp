import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";

const root = join(import.meta.dirname, "..");
const read = (relativePath) => readFileSync(join(root, relativePath), "utf8");

test("arquivos de runtime do projectmem permanecem descartáveis", () => {
  const gitignore = read(".gitignore");
  const tracked = execFileSync("git", ["ls-files", ".projectmem"], { cwd: root, encoding: "utf8" }).split("\n");

  for (const path of [
    ".projectmem/.current_issue",
    ".projectmem/events.jsonl",
    ".projectmem/watch.pid",
    ".projectmem/watch.log",
    ".projectmem/structure.json",
  ]) {
    assert.match(gitignore, new RegExp(`^${path.replaceAll(".", "\\.")}$`, "m"));
    assert.equal(tracked.includes(path), false, `${path} deve permanecer descartável`);
  }
});

test("o piloto é um sidecar de desenvolvimento com escopo explícito", () => {
  const runbook = read("docs/runbooks/projectmem.md");
  const decision = read("docs/adr/0005-memoria-operacional-de-desenvolvimento.md");

  for (const flag of [
    "--no-hooks",
    "--no-global",
    "--no-watch",
    "--no-backfill",
    "--no-claude-md",
    "--no-stack-detect",
    "--no-mcp-config",
    "--no-structure",
  ]) {
    assert.match(runbook, new RegExp(flag.replaceAll("-", "\\-")));
  }

  assert.match(runbook, /PJM_BIN/);
  assert.match(runbook, /PJM_ITERATIONS/);
  assert.match(decision, /Git e Markdown versionado continuam canônicos/);
  assert.match(decision, /Mem0 permanece adiado/);
  assert.equal(existsSync(join(root, "CLAUDE.md")), false);
});

test("projectmem não entra no produto nem no McpHost", () => {
  const packageJson = JSON.parse(read("package.json"));
  const dependencies = { ...packageJson.dependencies, ...packageJson.devDependencies };

  assert.equal(Object.hasOwn(dependencies, "projectmem"), false);
  assert.match(read("docs/runbooks/projectmem.md"), /não entra\s+no runtime do ERP/i);

  const productFiles = execFileSync(
    "git",
    ["ls-files", "src", "apps/web/src"],
    { cwd: root, encoding: "utf8" },
  ).trim().split("\n").filter(Boolean);

  for (const file of productFiles) {
    const contents = read(file);
    assert.doesNotMatch(contents, /projectmem|Mem0/i, `${file} não pode depender da memória de desenvolvimento`);
  }
});

test("as regras de memória são opt-in e importam somente contexto curado", () => {
  const rules = read("AGENTS.md");
  const instructions = read(".projectmem/AI_INSTRUCTIONS.md");
  const projectmemRunbook = read("docs/runbooks/projectmem.md");
  const basicMemoryRunbook = read("docs/runbooks/basic-memory.md");
  const importGuidePath = join(root, "docs/runbooks/github-memory-import.md");

  assert.equal(existsSync(importGuidePath), true);
  const importGuide = read("docs/runbooks/github-memory-import.md");

  assert.match(rules, /docs\/runbooks\/(projectmem|basic-memory)\.md/);
  assert.match(rules, /opt-in|autoriz/i);
  assert.match(instructions, /optional|opt-in/i);
  assert.doesNotMatch(instructions, /hooks installed|automatically capture|watcher daemon/i);
  assert.match(projectmemRunbook, /manifesto|dry-run/i);
  assert.match(projectmemRunbook, /allowlist/i);
  assert.match(projectmemRunbook, /deduplic/i);
  assert.match(importGuide, /github\.com\/jottakka\/vinx-erp\/issues\/108/);
  assert.match(importGuide, /github\.com\/jottakka\/vinx-erp\/issues\/127/);
  assert.match(importGuide, /github\.com\/jottakka\/vinx-erp\/issues\/110/);
  assert.match(importGuide, /corpo|comentários|do not copy/i);
  assert.match(basicMemoryRunbook, /vinx-erp-docs/);
  assert.match(basicMemoryRunbook, /<repository-root>/);
  assert.match(basicMemoryRunbook, /abrir|open/i);
});
