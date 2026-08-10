import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, join } from "node:path";
import { performance } from "node:perf_hooks";

const root = process.cwd();
const pjm = process.env.PJM_BIN ?? "pjm";
const rg = process.env.RG_BIN ?? "rg";
const iterations = Number.parseInt(process.env.PJM_ITERATIONS ?? "20", 10);
const initFlags = [
  "init",
  "--no-hooks",
  "--no-global",
  "--no-watch",
  "--no-backfill",
  "--no-claude-md",
  "--no-stack-detect",
  "--no-mcp-config",
  "--no-structure",
];

assert(Number.isInteger(iterations) && iterations > 0 && iterations <= 100, "PJM_ITERATIONS must be 1..100");

const run = (command, args, cwd) => {
  execFileSync(command, args, {
    cwd,
    encoding: "utf8",
    stdio: "ignore",
  });
};

const measure = (operation, action, count = iterations) => {
  const samples = [];
  for (let index = 0; index < count; index += 1) {
    const started = performance.now();
    action(index);
    samples.push(performance.now() - started);
  }
  return { operation, ...stats(samples), samples: samples.map((value) => Number(value.toFixed(3))) };
};

const stats = (values) => {
  const sorted = [...values].sort((left, right) => left - right);
  const percentile = (fraction) => sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * fraction) - 1)];
  return {
    n: sorted.length,
    min_ms: Number(sorted[0].toFixed(3)),
    p50_ms: Number(percentile(0.5).toFixed(3)),
    p95_ms: Number(percentile(0.95).toFixed(3)),
    max_ms: Number(sorted.at(-1).toFixed(3)),
  };
};

const createSandbox = () => {
  const sandbox = mkdtempSync(join(tmpdir(), "projectmem-benchmark-"));
  run("git", ["init", "-q"], sandbox);
  writeFileSync(join(sandbox, "README.md"), "Synthetic projectmem benchmark fixture.\n");
  return sandbox;
};

const benchmarkSandbox = () => {
  const sandbox = createSandbox();
  try {
    const started = performance.now();
    run(pjm, initFlags, sandbox);
    const coldInitMs = performance.now() - started;

    for (let index = 0; index < 100; index += 1) {
      run(pjm, ["note", `Synthetic benchmark event ${index}: priority and issue retrieval.`], sandbox);
    }

    const reads = Object.fromEntries(
      Object.entries({
        show: ["show"],
        brief: ["brief"],
        context_json: ["context", "--tokens", "2000", "--format", "json"],
        search: ["search", "priority"],
      }).map(([name, args]) => [
        name,
        measure(`pjm ${name} on 100-event sandbox`, () => run(pjm, args, sandbox)),
      ]),
    );

    const writes = measure(
      "pjm note on 100-event sandbox",
      (index) => run(pjm, ["note", `Synthetic measured event ${index}: safe operational fact.`], sandbox),
    );

    return {
      cold_init_ms: Number(coldInitMs.toFixed(3)),
      seeded_events: 100,
      reads,
      write: writes,
    };
  } finally {
    rmSync(sandbox, { force: true, recursive: true });
  }
};

const readCommands = {
  show: ["show"],
  brief: ["brief"],
  context_json: ["context", "--tokens", "2000", "--format", "json"],
  search: ["search", "projectmem"],
};

const warmReads = Object.fromEntries(
  Object.entries(readCommands).map(([name, args]) => [
    name,
    measure(`pjm ${name}`, () => run(pjm, args, root)),
  ]),
);

const rgBaseline = measure("rg docs memory/projectmem/issue", () => {
  run(rg, ["-n", "memory|projectmem|issue", "docs"], root);
});

const result = {
  project: basename(root),
  projectmem_version: process.env.PJM_VERSION ?? "unknown",
  node: process.version,
  platform: `${process.platform}-${process.arch}`,
  iterations,
  warm_reads: warmReads,
  rg_baseline: rgBaseline,
  sandbox: benchmarkSandbox(),
};

process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
