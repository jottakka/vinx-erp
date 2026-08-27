import assert from "node:assert/strict";
import { execFileSync, spawn, spawnSync } from "node:child_process";
import { EventEmitter } from "node:events";
import { existsSync } from "node:fs";
import { createConnection } from "node:net";
import path from "node:path";
import { PassThrough } from "node:stream";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  parseLauncherArgs,
  parseReadyOrigin,
  parseSharedReadiness,
  parseSingleReadiness,
  runLauncher,
} from "./run-real-e2e.mjs";

const REPOSITORY_ROOT = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const E2E_HOST_PROJECT =
  "tests/tools/Vinx.Erp.Foundation.E2eHost/Vinx.Erp.Foundation.E2eHost.csproj";
const E2E_HOST_OUTPUT_DIRECTORY = path.join(
  REPOSITORY_ROOT,
  "tests/tools/Vinx.Erp.Foundation.E2eHost/bin/Debug/net10.0",
);

function builtE2eHostDll() {
  const artifactNames = [
    "Vinx.Erp.Foundation.E2eHost.dll",
    "Vinx.Erp.Foundation.E2eHost.deps.json",
    "Vinx.Erp.Foundation.E2eHost.runtimeconfig.json",
  ];
  for (const artifactName of artifactNames) {
    assert.equal(
      existsSync(path.join(E2E_HOST_OUTPUT_DIRECTORY, artifactName)),
      true,
      `Build precondition: run dotnet build ${E2E_HOST_PROJECT} before this test.`,
    );
  }
  return path.join(E2E_HOST_OUTPUT_DIRECTORY, artifactNames[0]);
}

function startDirectE2eHost(profile) {
  const child = spawn("dotnet", [builtE2eHostDll(), "--profile", profile], {
    cwd: REPOSITORY_ROOT,
    env: process.env,
    stdio: ["ignore", "pipe", "pipe"],
  });
  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");
  const closed = new Promise((resolve) => child.once("close", resolve));
  let standardOutput = "";
  let standardError = "";
  let parsedOutputLength = 0;
  let resolveReady;
  let rejectReady;
  const ready = new Promise((resolve, reject) => {
    resolveReady = resolve;
    rejectReady = reject;
  });
  let readySettled = false;
  const settleReady = (operation) => {
    if (readySettled) return;
    readySettled = true;
    operation();
  };
  let readinessOrigin;
  let readinessSentinelSeen = false;
  child.stdout.on("data", (chunk) => {
    standardOutput += chunk;
    let newlineIndex;
    while ((newlineIndex = standardOutput.indexOf("\n", parsedOutputLength)) >= 0) {
      const line = standardOutput
        .slice(parsedOutputLength, newlineIndex)
        .replace(/\r$/, "");
      parsedOutputLength = newlineIndex + 1;
      if (line.startsWith("E2E_API_ORIGIN=")) {
        try {
          readinessOrigin = parseReadyOrigin(line);
        } catch (error) {
          settleReady(() => rejectReady(error));
        }
      } else if (line === "E2E_SINGLE_READY=1") {
        readinessSentinelSeen = true;
      }

      if (readinessOrigin !== undefined && readinessSentinelSeen) {
        settleReady(() => resolveReady(readinessOrigin));
      }
    }
  });
  child.stderr.on("data", (chunk) => {
    standardError += chunk;
  });
  let resolveOutcome;
  const outcome = new Promise((resolve) => {
    resolveOutcome = resolve;
  });
  child.once("error", () => {
    settleReady(() => rejectReady(new Error("direct E2E host spawn failed")));
    resolveOutcome({ code: null, kind: "error", signal: null });
  });
  child.once("exit", (code, signal) => {
    settleReady(() => rejectReady(new Error("direct E2E host exited before readiness")));
    resolveOutcome({ code, kind: "exit", signal });
  });
  return {
    child,
    closed,
    outcome,
    output: () => ({ standardError, standardOutput }),
    ready,
  };
}

async function stopDirectE2eHost(child, outcome) {
  if (child.exitCode !== null || child.signalCode !== null) return outcome;
  child.kill("SIGTERM");
  try {
    return await withTestTimeout(outcome, 30_000);
  } catch {
    child.kill("SIGKILL");
    return withTestTimeout(outcome, 5_000);
  }
}

function createFakeChild(label, pid) {
  const child = new EventEmitter();
  child.label = label;
  child.pid = pid;
  child.stderr = new PassThrough();
  child.stdout = new PassThrough();
  child.exitCode = null;
  child.signalCode = null;
  child.finish = (code = 0, signal = null) => {
    if (child.exitCode !== null || child.signalCode !== null) return;
    child.exitCode = code;
    child.signalCode = signal;
    child.stderr.end();
    child.stdout.end();
    child.emit("exit", code, signal);
  };
  return child;
}

function singleReadinessLines(origin = "http://127.0.0.1:54321") {
  return `E2E_API_ORIGIN=${origin}\nE2E_SINGLE_READY=1\n`;
}

function createHarness(overrides = {}) {
  const {
    onHostSpawn,
    onReady,
    onViteSpawn,
    ...dependencyOverrides
  } = overrides;
  const host = createFakeChild("host", 101);
  const vite = createFakeChild("vite", 202);
  const signalTarget = new EventEmitter();
  const events = [];
  const spawns = [];
  const output = [];
  const recordReady = (lines) => {
    output.push(...lines);
    setImmediate(onReady ?? (() => signalTarget.emit("SIGINT")));
  };

  const deps = {
    args: ["--profile", "maria"],
    repositoryRoot: "/repo",
    platform: "darwin",
    processExecPath: "/opt/node/bin/node",
    environment: { KEEP_ME: "yes" },
    readinessTimeoutMs: 30,
    shutdownTimeoutMs: 30,
    checkWebPort: async () => {
      events.push("port-checked");
    },
    spawnProcess: (command, args, options) => {
      const child = spawns.length === 0 ? host : vite;
      spawns.push({ command, args, options });
      events.push(`spawn:${child.label}`);

      queueMicrotask(() => {
        child.emit("spawn");
        if (child === host) {
          (onHostSpawn ?? (() =>
            host.stdout.write(singleReadinessLines())))();
        } else {
          onViteSpawn?.();
        }
      });

      return child;
    },
    waitForHttp: async (url) => {
      events.push(`http:${url}`);
    },
    signalTarget,
    killProcessGroup: (child, signal) => {
      events.push(`kill:${child.label}:${signal}`);
      setTimeout(() => {
        events.push(`exit:${child.label}`);
        child.finish(child === host ? 0 : null, child === host ? null : signal);
      }, 1);
    },
    publishReady: ({ browserUrl, profile }) =>
      recordReady([
        `E2E_BROWSER_URL=${browserUrl}`,
        `E2E_PROFILE=${profile}`,
      ]),
    ...dependencyOverrides,
  };

  return { deps, events, host, output, signalTarget, spawns, vite };
}

function sharedReadinessLines() {
  return [
    "E2E_API_ORIGIN_MARIA=http://127.0.0.1:54321",
    "E2E_API_ORIGIN_CLARA=http://127.0.0.1:54324",
    "E2E_DB_HOST=127.0.0.1",
    "E2E_DB_PORT=54323",
    "E2E_DB_DATABASE=vinx_fixture_a1b2c3",
    "E2E_DB_USER=postgres",
    "E2E_DB_PASSWORD_FILE=/tmp/vinx-e2e-check/postgres-password",
    "E2E_SHARED_READY=1",
  ];
}

function createSharedHarness(overrides = {}) {
  const { onHostSpawn, onReady, ...dependencyOverrides } = overrides;
  const host = createFakeChild("host", 101);
  const mariaVite = createFakeChild("vite-maria", 202);
  const claraVite = createFakeChild("vite-clara", 303);
  const signalTarget = new EventEmitter();
  const events = [];
  const spawns = [];
  const output = [];
  const children = [host, mariaVite, claraVite];
  const deps = {
    args: ["--profiles", "maria,clara"],
    repositoryRoot: "/repo",
    platform: "darwin",
    processExecPath: "/opt/node/bin/node",
    environment: { KEEP_ME: "yes" },
    readinessTimeoutMs: 30,
    shutdownTimeoutMs: 30,
    checkWebPort: async (_host, port) => {
      events.push(`port-checked:${port}`);
    },
    spawnProcess: (command, args, options) => {
      const child = children[spawns.length];
      spawns.push({ command, args, options });
      events.push(`spawn:${child.label}`);
      queueMicrotask(() => {
        child.emit("spawn");
        if (child === host) {
          (onHostSpawn ?? (() =>
            host.stdout.write(`${sharedReadinessLines().join("\n")}\n`)))();
        }
      });
      return child;
    },
    waitForHttp: async (url) => {
      events.push(`http:${url}`);
    },
    signalTarget,
    killProcessGroup: (child, signal) => {
      events.push(`kill:${child.label}:${signal}`);
      setTimeout(() => {
        events.push(`exit:${child.label}`);
        child.finish(child === host ? 0 : null, child === host ? null : signal);
      }, 1);
    },
    publishReady: (details) => {
      output.push(
        `E2E_BROWSER_URL_MARIA=${details.mariaBrowserUrl}`,
        "E2E_PROFILE_MARIA=maria",
        `E2E_BROWSER_URL_CLARA=${details.claraBrowserUrl}`,
        "E2E_PROFILE_CLARA=clara",
        `E2E_DB_HOST=${details.databaseHost}`,
        `E2E_DB_PORT=${details.databasePort}`,
        `E2E_DB_DATABASE=${details.databaseName}`,
        `E2E_DB_USER=${details.databaseUser}`,
        `E2E_DB_PASSWORD_FILE=${details.databasePasswordFilePath}`,
      );
      setImmediate(onReady ?? (() => signalTarget.emit("SIGINT")));
    },
    ...dependencyOverrides,
  };

  return {
    claraVite,
    deps,
    events,
    host,
    mariaVite,
    output,
    signalTarget,
    spawns,
  };
}

function withTestTimeout(promise, milliseconds = 75) {
  let timeout;
  return Promise.race([
    promise,
    new Promise((_, reject) =>
      (timeout = setTimeout(
        () => reject(new Error("test lifecycle timeout")),
        milliseconds,
      )),
    ),
  ]).finally(() => clearTimeout(timeout));
}

function runningFixtureContainerIds() {
  const output = execFileSync(
    "docker",
    [
      "ps",
      "--filter",
      "ancestor=postgres:17.9-alpine",
      "--format",
      "{{.ID}}",
    ],
    { encoding: "utf8" },
  );
  return new Set(output.split("\n").filter(Boolean));
}

async function pollUntil(condition, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await condition()) return true;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  return false;
}

function isPortOpen(port) {
  return new Promise((resolve) => {
    const socket = createConnection({ host: "127.0.0.1", port });
    let done = false;
    const finish = (open) => {
      if (done) return;
      done = true;
      socket.destroy();
      resolve(open);
    };
    socket.setTimeout(250, () => finish(false));
    socket.once("connect", () => finish(true));
    socket.once("error", () => finish(false));
  });
}

test("parseLauncherArgs accepts each allow-listed profile", () => {
  for (const profile of ["maria", "beatriz", "joao", "clara"]) {
    assert.deepEqual(parseLauncherArgs(["--profile", profile]), {
      mode: "single",
      profile,
    });
  }
});

test("parseLauncherArgs accepts only the exact shared Maria and Clara request", () => {
  assert.deepEqual(parseLauncherArgs(["--profiles", "maria,clara"]), {
    mode: "shared",
    profiles: ["maria", "clara"],
  });
});

test("parseLauncherArgs rejects missing, extra, duplicate, and unknown arguments", () => {
  for (const args of [
    [],
    ["--profile"],
    ["--profile", "maria", "extra"],
    ["--profile", "maria", "--profile", "clara"],
    ["--profile", "desconhecido"],
    ["--profiles", "maria"],
    ["--profiles", "clara,maria"],
    ["--profiles", "maria, clara"],
    ["--profiles", "maria,clara,joao"],
    ["--profiles", "maria,clara", "--profile", "maria"],
  ]) {
    assert.throws(
      () => parseLauncherArgs(args),
      /E2E launcher failed: invalid_arguments/,
    );
  }
});

test("direct E2E host rejects unknown arguments without output or readiness", () => {
  const result = spawnSync(
    "dotnet",
    [builtE2eHostDll(), "--profile", "unknown"],
    {
      cwd: REPOSITORY_ROOT,
      encoding: "utf8",
      env: process.env,
      stdio: ["ignore", "pipe", "pipe"],
    },
  );

  assert.equal(result.error, undefined);
  assert.equal(result.status, 2);
  assert.equal(result.signal, null);
  assert.equal(result.stdout, "");
  assert.equal(result.stderr, "");
  assert.doesNotMatch(result.stdout, /E2E_API_ORIGIN=/);
});

test("parseReadyOrigin accepts only an exact loopback HTTP origin", () => {
  assert.equal(
    parseReadyOrigin("E2E_API_ORIGIN=http://127.0.0.1:54321"),
    "http://127.0.0.1:54321",
  );

  for (const line of [
    "E2E_API_ORIGIN=http://localhost:54321",
    "E2E_API_ORIGIN=https://127.0.0.1:54321",
    "E2E_API_ORIGIN=http://127.0.0.1:0",
    "E2E_API_ORIGIN=http://127.0.0.1:65536",
    "E2E_API_ORIGIN=http://127.0.0.1:54321/health",
    "not-ready",
  ]) {
    assert.throws(
      () => parseReadyOrigin(line),
      /E2E launcher failed: invalid_host_readiness/,
    );
  }
});

test("parseSharedReadiness accepts exactly one complete ordered shared block", () => {
  assert.deepEqual(parseSharedReadiness(sharedReadinessLines()), {
    mariaApiOrigin: "http://127.0.0.1:54321",
    claraApiOrigin: "http://127.0.0.1:54324",
    databaseHost: "127.0.0.1",
    databasePort: 54323,
    databaseName: "vinx_fixture_a1b2c3",
    databaseUser: "postgres",
    databasePasswordFilePath: "/tmp/vinx-e2e-check/postgres-password",
  });
});

test("parseSingleReadiness accepts exactly the origin and terminal sentinel block", () => {
  assert.equal(
    parseSingleReadiness([
      "E2E_API_ORIGIN=http://127.0.0.1:54321",
      "E2E_SINGLE_READY=1",
    ]),
    "http://127.0.0.1:54321",
  );
});

test("parseSingleReadiness rejects incomplete, out-of-order, and unsafe blocks", () => {
  for (const lines of [
    [],
    ["E2E_API_ORIGIN=http://127.0.0.1:54321"],
    ["E2E_API_ORIGIN=http://127.0.0.1:54321", "E2E_SINGLE_READY=1", "extra"],
    ["E2E_API_ORIGIN=http://127.0.0.1:54321", "E2E_SINGLE_READY=0"],
    ["E2E_API_ORIGIN=http://127.0.0.1:54321", "E2E_SHARED_READY=1"],
    ["E2E_SINGLE_READY=1", "E2E_API_ORIGIN=http://127.0.0.1:54321"],
    ["E2E_API_ORIGIN=http://example.test:54321", "E2E_SINGLE_READY=1"],
    "E2E_API_ORIGIN=http://127.0.0.1:54321",
  ]) {
    assert.throws(
      () => parseSingleReadiness(lines),
      /E2E launcher failed: invalid_host_readiness/,
    );
  }
});

test("shared launcher ignores host build preamble before its contiguous readiness block", async () => {
  const harness = createSharedHarness({
    onHostSpawn: () => {
      harness.host.stdout.write("Determining projects to restore...\n");
      harness.host.stdout.write("Build succeeded.\n");
      harness.host.stdout.write(`${sharedReadinessLines().join("\n")}\n`);
    },
  });

  assert.equal(await runLauncher(harness.deps), 0);
  assert.equal(harness.spawns.length, 3);
});

test("single launcher ignores host build preamble before its contiguous readiness block", async () => {
  const harness = createHarness({
    onHostSpawn: () => {
      harness.host.stdout.write("Determining projects to restore...\n");
      harness.host.stdout.write("Build succeeded. 0 Error(s)\n");
      harness.host.stdout.write(singleReadinessLines());
    },
  });

  assert.equal(await runLauncher(harness.deps), 0);
  assert.equal(harness.spawns.length, 2);
});

test("single launcher rejects an unexpected E2E line before its readiness block", async () => {
  const harness = createHarness({
    onHostSpawn: () => {
      harness.host.stdout.write("E2E_DB_HOST=127.0.0.1\n");
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /E2E launcher failed: invalid_host_readiness/,
  );
  assert.equal(harness.spawns.length, 1);
});

test("single launcher rejects noise between the origin and the readiness sentinel", async () => {
  const harness = createHarness({
    onHostSpawn: () => {
      harness.host.stdout.write("E2E_API_ORIGIN=http://127.0.0.1:54321\n");
      harness.host.stdout.write("noise between readiness lines\n");
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /E2E launcher failed: invalid_host_readiness/,
  );
  assert.equal(harness.spawns.length, 1);
});

test("single launcher times out when the origin is not followed by the sentinel", async () => {
  const harness = createHarness({
    readinessTimeoutMs: 5,
    onHostSpawn: () => {
      harness.host.stdout.write("E2E_API_ORIGIN=http://127.0.0.1:54321\n");
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /E2E launcher failed: host_readiness_timeout/,
  );
  assert.equal(harness.spawns.length, 1);
});

test("launcher preserves a typed host failure while the process exits shortly after it", async () => {
  const harness = createHarness({
    onHostSpawn: () => {
      harness.host.stderr.write("E2E_HOST_FAILED=seed_failed\n");
      setImmediate(() => harness.host.finish(1));
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /^Error: E2E launcher failed: host_seed_failed$/,
  );
  assert.deepEqual(harness.output, []);
  assert.equal(
    harness.events.some((event) => event.startsWith("kill:host:")),
    false,
  );
});

test("launcher reconciles stdout closure before a typed host failure", async () => {
  const harness = createHarness({
    onHostSpawn: () => {
      harness.host.stdout.end();
      setImmediate(() => {
        harness.host.stderr.write("E2E_HOST_FAILED=seed_failed\n");
        setImmediate(() => harness.host.finish(1));
      });
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /^Error: E2E launcher failed: host_seed_failed$/,
  );
  assert.equal(
    harness.events.some((event) => event.startsWith("kill:host:")),
    false,
  );
});

test("launcher ignores arbitrary host stderr and retains a generic failure", async () => {
  const harness = createHarness({
    onHostSpawn: () => {
      harness.host.stderr.write("sensitive host exception details\n");
      harness.host.finish(1);
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /^Error: E2E launcher failed: host_exit$/,
  );
  assert.deepEqual(harness.output, []);
  assert.equal(
    harness.events.some((event) => event.startsWith("kill:host:")),
    false,
  );
});

test("launcher reconciles a typed host failure after readiness", async () => {
  const harness = createHarness({
    onReady: () => {
      harness.host.stderr.write("E2E_HOST_FAILED=seed_failed\n");
      setImmediate(() => harness.host.finish(1));
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /^Error: E2E launcher failed: host_seed_failed$/,
  );
  assert.equal(
    harness.events.some((event) => event.startsWith("kill:host:")),
    false,
  );
});

test("parseSharedReadiness rejects duplicate, incomplete, out-of-order, and unsafe blocks", () => {
  const complete = sharedReadinessLines();
  const duplicate = [...complete];
  duplicate.splice(1, 0, complete[0]);
  const missingMarker = complete.slice(0, -1);
  const markerTooEarly = [complete.at(-1), ...complete.slice(0, -1)];
  const unsafe = [...complete];
  unsafe[4] = "E2E_CONNECTION_STRING=must-not-be-accepted";

  for (const lines of [duplicate, missingMarker, markerTooEarly, unsafe]) {
    assert.throws(
      () => parseSharedReadiness(lines),
      /E2E launcher failed: invalid_host_readiness/,
    );
  }
});

test("shared launcher starts one host and two Vites on the fixed Maria and Clara ports", async () => {
  const harness = createSharedHarness();

  assert.equal(await runLauncher(harness.deps), 0);
  assert.deepEqual(
    harness.events.filter((event) => event.startsWith("port-checked:")),
    ["port-checked:4174", "port-checked:4175"],
  );
  assert.equal(harness.spawns.length, 3);
  assert.deepEqual(harness.spawns[0].args.slice(-2), ["--profiles", "maria,clara"]);
  assert.deepEqual(harness.spawns[1].args.slice(-3), ["--port", "4174", "--strictPort"]);
  assert.deepEqual(harness.spawns[2].args.slice(-3), ["--port", "4175", "--strictPort"]);
  assert.equal(harness.spawns[1].options.env.VINX_API_ORIGIN, "http://127.0.0.1:54321");
  assert.equal(harness.spawns[2].options.env.VINX_API_ORIGIN, "http://127.0.0.1:54324");
  assert.deepEqual(harness.output, [
    "E2E_BROWSER_URL_MARIA=http://127.0.0.1:4174",
    "E2E_PROFILE_MARIA=maria",
    "E2E_BROWSER_URL_CLARA=http://127.0.0.1:4175",
    "E2E_PROFILE_CLARA=clara",
    "E2E_DB_HOST=127.0.0.1",
    "E2E_DB_PORT=54323",
    "E2E_DB_DATABASE=vinx_fixture_a1b2c3",
    "E2E_DB_USER=postgres",
    "E2E_DB_PASSWORD_FILE=/tmp/vinx-e2e-check/postgres-password",
  ]);
  assert.deepEqual(
    harness.events.filter((event) => event.startsWith("kill:")),
    [
      "kill:vite-clara:SIGINT",
      "kill:vite-maria:SIGINT",
      "kill:host:SIGINT",
    ],
  );
});

test("shared launcher withholds public readiness until both Vites answer HTTP", async () => {
  const waiters = new Map();
  const harness = createSharedHarness({
    waitForHttp: (url) =>
      new Promise((resolve) => {
        waiters.set(url, resolve);
      }),
  });
  const launcher = runLauncher(harness.deps);

  assert.equal(
    await pollUntil(() => waiters.has("http://127.0.0.1:4174"), 500),
    true,
  );
  assert.deepEqual(harness.output, []);
  waiters.get("http://127.0.0.1:4174")();
  assert.equal(
    await pollUntil(() => waiters.has("http://127.0.0.1:4175"), 500),
    true,
  );
  assert.deepEqual(harness.output, []);
  waiters.get("http://127.0.0.1:4175")();

  assert.equal(await launcher, 0);
  assert.equal(harness.output.length, 9);
});

test("shared host cleanup exit is reported as child_cleanup_failed", async () => {
  const harness = createSharedHarness({
    killProcessGroup: (child, signal) => {
      harness.events.push(`kill:${child.label}:${signal}`);
      setTimeout(() => {
        harness.events.push(`exit:${child.label}`);
        child.finish(child === harness.host ? 1 : null, child === harness.host ? null : signal);
      }, 1);
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /^Error: E2E launcher failed: child_cleanup_failed$/,
  );
});

test("shared launcher preserves an already-exited host as the primary failure", async () => {
  const harness = createSharedHarness({
    onReady: () => harness.host.finish(0),
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /^Error: E2E launcher failed: host_exit$/,
  );
});

test("shared launcher accepts a zero-code or forwarded-signal host exit for SIGINT and SIGTERM", async () => {
  for (const signal of ["SIGINT", "SIGTERM"]) {
    for (const hostExit of ["zero-code", "forwarded-signal"]) {
      const harness = createSharedHarness({
        onReady: () => harness.signalTarget.emit(signal),
        killProcessGroup: (child, forwardedSignal) => {
          harness.events.push(`kill:${child.label}:${forwardedSignal}`);
          setTimeout(() => {
            harness.events.push(`exit:${child.label}`);
            child.finish(
              child === harness.host && hostExit === "zero-code" ? 0 : null,
              child === harness.host
                ? (hostExit === "forwarded-signal" ? forwardedSignal : null)
                : forwardedSignal,
            );
          }, 1);
        },
      });

      assert.equal(await runLauncher(harness.deps), 0);
      assert.ok(harness.events.includes(`kill:host:${signal}`));
    }
  }
});

test("shared launcher rejects a host exit by a signal other than the forwarded one", async () => {
  const harness = createSharedHarness({
    killProcessGroup: (child, forwardedSignal) => {
      harness.events.push(`kill:${child.label}:${forwardedSignal}`);
      setTimeout(() => {
        harness.events.push(`exit:${child.label}`);
        child.finish(
          null,
          child === harness.host ? "SIGHUP" : forwardedSignal,
        );
      }, 1);
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /^Error: E2E launcher failed: child_cleanup_failed$/,
  );
});

test("runLauncher accepts a host terminated by the forwarded shutdown signal", async () => {
  for (const signal of ["SIGINT", "SIGTERM"]) {
    const harness = createHarness({
      onReady: () => harness.signalTarget.emit(signal),
      killProcessGroup: (child, forwardedSignal) => {
        harness.events.push(`kill:${child.label}:${forwardedSignal}`);
        setTimeout(() => {
          harness.events.push(`exit:${child.label}`);
          child.finish(null, forwardedSignal);
        }, 1);
      },
    });

    assert.equal(await withTestTimeout(runLauncher(harness.deps)), 0);
    assert.ok(harness.events.includes(`kill:host:${signal}`));
  }
});

test("runLauncher preserves the primary failure when the host group already vanished", async () => {
  const harness = createHarness({
    readinessTimeoutMs: 5,
    spawnProcess: (command, args, options) => {
      harness.spawns.push({ command, args, options });
      queueMicrotask(() => harness.host.emit("spawn"));
      return harness.host;
    },
    killProcessGroup: () => {
      const error = new Error("process group does not exist");
      error.code = "ESRCH";
      throw error;
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /E2E launcher failed: host_readiness_timeout/,
  );
  assert.equal(harness.spawns.length, 1);
});

test("shared launcher rejects a SIGKILL host exit with no exit code", async () => {
  const harness = createSharedHarness({
    shutdownTimeoutMs: 5,
    killProcessGroup: (child, signal) => {
      harness.events.push(`kill:${child.label}:${signal}`);
      if (child === harness.host && signal !== "SIGKILL") return;
      setTimeout(() => {
        harness.events.push(`exit:${child.label}`);
        child.finish(
          null,
          child === harness.host ? "SIGKILL" : signal,
        );
      }, 1);
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /^Error: E2E launcher failed: child_cleanup_failed$/,
  );
});

test("shared launcher preserves a host exit that follows readiness", async () => {
  const harness = createSharedHarness({
    onReady: () => harness.host.finish(1),
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /^Error: E2E launcher failed: host_exit$/,
  );
});

test("runLauncher does not start Vite when the host exits before readiness", async () => {
  const harness = createHarness({
    spawnProcess: (command, args, options) => {
      harness.spawns.push({ command, args, options });
      queueMicrotask(() => {
        harness.host.emit("spawn");
        harness.host.finish(1);
      });
      return harness.host;
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /E2E launcher failed: host_exit/,
  );
  assert.equal(harness.spawns.length, 1);
});

test("runLauncher rejects invalid host readiness before starting Vite", async () => {
  const harness = createHarness({
    spawnProcess: (command, args, options) => {
      harness.spawns.push({ command, args, options });
      queueMicrotask(() => {
        harness.host.emit("spawn");
        harness.host.stdout.write(
          singleReadinessLines("http://example.test:54321"),
        );
      });
      return harness.host;
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /E2E launcher failed: invalid_host_readiness/,
  );
  assert.equal(harness.spawns.length, 1);
});

test("runLauncher keeps draining host stdout after readiness without relaying it", async () => {
  let acceptedEveryWrite = true;
  const harness = createHarness({
    waitForHttp: async () => {
      const chunk = Buffer.alloc(4_096, 120);
      for (let index = 0; index < 64; index += 1) {
        await new Promise((resolve) => setImmediate(resolve));
        acceptedEveryWrite = harness.host.stdout.write(chunk) && acceptedEveryWrite;
      }
    },
  });

  await runLauncher(harness.deps);

  assert.equal(acceptedEveryWrite, true);
  assert.deepEqual(harness.output, [
    "E2E_BROWSER_URL=http://127.0.0.1:4174",
    "E2E_PROFILE=maria",
  ]);
});

test("runLauncher terminates and awaits the host group after readiness timeout", async () => {
  const harness = createHarness({
    readinessTimeoutMs: 5,
    spawnProcess: (command, args, options) => {
      harness.spawns.push({ command, args, options });
      queueMicrotask(() => harness.host.emit("spawn"));
      return harness.host;
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /E2E launcher failed: host_readiness_timeout/,
  );
  assert.deepEqual(
    harness.events.filter((event) => event.startsWith("kill:") || event.startsWith("exit:")),
    ["kill:host:SIGTERM", "exit:host"],
  );
});

test("runLauncher reports cleanup failure over a preceding startup failure", async () => {
  const harness = createHarness({
    readinessTimeoutMs: 5,
    spawnProcess: (command, args, options) => {
      harness.spawns.push({ command, args, options });
      queueMicrotask(() => harness.host.emit("spawn"));
      return harness.host;
    },
    killProcessGroup: () => {
      throw new Error("/sensitive/cleanup/path");
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /^Error: E2E launcher failed: child_cleanup_failed$/,
  );
});

test("runLauncher forwards a signal received during host startup", async () => {
  const harness = createHarness({
    onHostSpawn: () => harness.signalTarget.emit("SIGINT"),
    readinessTimeoutMs: 50,
  });

  await runLauncher(harness.deps);

  assert.equal(harness.spawns.length, 1);
  assert.deepEqual(
    harness.events.filter((event) => event.startsWith("kill:") || event.startsWith("exit:")),
    ["kill:host:SIGINT", "exit:host"],
  );
});

for (const exitedChild of ["host", "vite"]) {
  test(`runLauncher stops the survivor when ${exitedChild} exits during Vite readiness`, async () => {
    const harness = createHarness({
      onViteSpawn: () => harness[exitedChild].finish(1),
      waitForHttp: () => new Promise(() => {}),
    });

    await assert.rejects(
      withTestTimeout(runLauncher(harness.deps)),
      new RegExp(`E2E launcher failed: ${exitedChild}_exit`),
    );
    assert.deepEqual(harness.output, []);
    assert.ok(
      harness.events.includes(
        `kill:${exitedChild === "host" ? "vite" : "host"}:SIGTERM`,
      ),
    );
  });
}

for (const exitedChild of ["host", "vite"]) {
  test(`runLauncher stops the survivor when ${exitedChild} exits after readiness`, async () => {
    const harness = createHarness({
      onReady: () => harness[exitedChild].finish(1),
    });

    await assert.rejects(
      withTestTimeout(runLauncher(harness.deps)),
      new RegExp(`E2E launcher failed: ${exitedChild}_exit`),
    );
    assert.ok(
      harness.events.includes(
        `kill:${exitedChild === "host" ? "vite" : "host"}:SIGTERM`,
      ),
    );
  });
}

test("runLauncher forwards a signal received during Vite readiness", async () => {
  let readinessAborted = false;
  const harness = createHarness({
    waitForHttp: (_url, _timeout, signal) =>
      new Promise((_, reject) => {
        signal.addEventListener(
          "abort",
          () => {
            readinessAborted = true;
            reject(new Error("cancelled fake readiness"));
          },
          { once: true },
        );
        queueMicrotask(() => harness.signalTarget.emit("SIGTERM"));
      }),
  });

  await withTestTimeout(runLauncher(harness.deps));

  assert.deepEqual(harness.output, []);
  assert.equal(readinessAborted, true);
  assert.deepEqual(
    harness.events.filter((event) => event.startsWith("kill:")),
    ["kill:vite:SIGTERM", "kill:host:SIGTERM"],
  );
});

test("runLauncher keeps signal handlers through cleanup and absorbs repeats", async () => {
  const listenerCounts = [];
  const harness = createHarness({
    killProcessGroup: (child, signal) => {
      harness.events.push(`kill:${child.label}:${signal}`);
      setTimeout(
        () => child.finish(child === harness.host ? 0 : null, child === harness.host ? null : signal),
        10,
      );
    },
    onReady: () => {
      harness.signalTarget.emit("SIGINT");
      setImmediate(() => {
        listenerCounts.push(
          harness.signalTarget.listenerCount("SIGINT"),
          harness.signalTarget.listenerCount("SIGTERM"),
        );
        harness.signalTarget.emit("SIGINT");
        harness.signalTarget.emit("SIGTERM");
      });
    },
  });

  await withTestTimeout(runLauncher(harness.deps), 100);

  assert.deepEqual(listenerCounts, [1, 1]);
  assert.equal(harness.signalTarget.listenerCount("SIGINT"), 0);
  assert.equal(harness.signalTarget.listenerCount("SIGTERM"), 0);
});

test("runLauncher maps a host error before spawn without leaking details", async () => {
  const harness = createHarness({
    spawnProcess: (command, args, options) => {
      harness.spawns.push({ command, args, options });
      queueMicrotask(() =>
        harness.host.emit("error", new Error("/sensitive/host/path")),
      );
      return harness.host;
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /^Error: E2E launcher failed: host_spawn_failed$/,
  );
  assert.equal(harness.spawns.length, 1);
});

test("runLauncher maps a Vite error before spawn without leaking details", async () => {
  const harness = createHarness({
    spawnProcess: (command, args, options) => {
      const child = harness.spawns.length === 0 ? harness.host : harness.vite;
      harness.spawns.push({ command, args, options });
      queueMicrotask(() => {
        if (child === harness.host) {
          child.emit("spawn");
          child.stdout.write(singleReadinessLines());
        } else {
          child.emit("error", new Error("/sensitive/vite/path"));
        }
      });
      return child;
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /^Error: E2E launcher failed: vite_spawn_failed$/,
  );
  assert.equal(harness.spawns.length, 2);
});

test("runLauncher escalates a child that ignores graceful shutdown", async () => {
  const harness = createHarness({
    shutdownTimeoutMs: 5,
    killProcessGroup: (child, signal) => {
      harness.events.push(`kill:${child.label}:${signal}`);
      if (child === harness.host) {
        setTimeout(() => child.finish(0), 1);
      } else if (signal === "SIGKILL") {
        setTimeout(() => child.finish(null, signal), 1);
      }
    },
  });

  await runLauncher(harness.deps);

  assert.deepEqual(
    harness.events.filter((event) => event.startsWith("kill:")),
    [
      "kill:vite:SIGINT",
      "kill:vite:SIGKILL",
      "kill:host:SIGINT",
    ],
  );
});

test("runLauncher starts exact child commands and shuts Vite down before host", async () => {
  const harness = createHarness();

  const exitCode = await runLauncher(harness.deps);

  assert.equal(exitCode, 0);
  assert.deepEqual(harness.spawns[0], {
    command: "dotnet",
    args: [
      "run",
      "--project",
      "/repo/tests/tools/Vinx.Erp.Foundation.E2eHost/Vinx.Erp.Foundation.E2eHost.csproj",
      "--no-launch-profile",
      "--",
      "--profile",
      "maria",
    ],
    options: {
      cwd: "/repo",
      detached: true,
      env: { KEEP_ME: "yes" },
      stdio: ["ignore", "pipe", "pipe"],
    },
  });
  assert.deepEqual(harness.spawns[1], {
    command: "/opt/node/bin/node",
    args: [
      "/repo/node_modules/vite/bin/vite.js",
      "--host",
      "127.0.0.1",
      "--port",
      "4174",
      "--strictPort",
    ],
    options: {
      cwd: "/repo/apps/web",
      detached: true,
      env: {
        KEEP_ME: "yes",
        VINX_API_ORIGIN: "http://127.0.0.1:54321",
      },
      stdio: ["ignore", "ignore", "ignore"],
    },
  });
  assert.deepEqual(harness.output, [
    "E2E_BROWSER_URL=http://127.0.0.1:4174",
    "E2E_PROFILE=maria",
  ]);
  assert.deepEqual(
    harness.events.filter((event) => event.startsWith("kill:") || event.startsWith("exit:")),
    [
      "kill:vite:SIGINT",
      "exit:vite",
      "kill:host:SIGINT",
      "exit:host",
    ],
  );
});

test("runLauncher checks the browser port before spawning either child", async () => {
  const harness = createHarness({
    checkWebPort: async () => {
      throw new Error("sensitive operating system detail");
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /E2E launcher failed: web_port_unavailable/,
  );
  assert.equal(harness.spawns.length, 0);
});

test(
  "direct E2E host smoke handles SIGTERM and removes its owned resources",
  {
    skip: process.env.VINX_RUN_REAL_E2E_HOST_SMOKE !== "1",
    timeout: 180_000,
  },
  async () => {
    const baselineContainers = runningFixtureContainerIds();
    const direct = startDirectE2eHost("maria");
    let apiOrigin;
    let apiPort;
    let exitResult;
    let ownedContainerId;

    try {
      apiOrigin = await withTestTimeout(
        Promise.race([
          direct.ready,
          direct.outcome.then(() => {
            throw new Error("direct E2E host exited before readiness");
          }),
        ]),
        120_000,
      );
      apiPort = Number(new URL(apiOrigin).port);
      assert.equal((await fetch(`${apiOrigin}/health`)).status, 200);
      assert.equal(
        await pollUntil(() => {
          const added = [...runningFixtureContainerIds()].filter(
            (id) => !baselineContainers.has(id),
          );
          if (added.length !== 1) return false;
          [ownedContainerId] = added;
          return true;
        }, 5_000),
        true,
      );

      assert.equal(direct.child.kill("SIGTERM"), true);
      exitResult = await withTestTimeout(direct.outcome, 30_000);
      assert.deepEqual(exitResult, { code: 0, kind: "exit", signal: null });
      await withTestTimeout(direct.closed, 5_000);
    } finally {
      if (exitResult === undefined) {
        exitResult = await stopDirectE2eHost(direct.child, direct.outcome);
        await withTestTimeout(direct.closed, 5_000);
      }
    }

    const output = direct.output();
    assert.equal(
      output.standardOutput,
      `E2E_API_ORIGIN=${apiOrigin}\nE2E_SINGLE_READY=1\n`,
    );
    assert.equal(output.standardError, "");
    assert.equal(
      output.standardOutput.match(/E2E_API_ORIGIN=/g)?.length,
      1,
    );
    assert.equal(
      output.standardOutput.match(/E2E_SINGLE_READY=/g)?.length,
      1,
    );
    assert.equal(
      await pollUntil(async () => !(await isPortOpen(apiPort)), 15_000),
      true,
    );
    assert.equal(
      await pollUntil(
        () => !runningFixtureContainerIds().has(ownedContainerId),
        30_000,
      ),
      true,
    );
  },
);

test(
  "real launcher smoke closes both ports and its PostgreSQL Testcontainer",
  {
    skip: process.env.VINX_RUN_REAL_E2E_SMOKE !== "1",
    timeout: 180_000,
  },
  async () => {
    const baselineContainers = runningFixtureContainerIds();
    const signalTarget = new EventEmitter();
    let resolveReady;
    const ready = new Promise((resolve) => {
      resolveReady = resolve;
    });
    const launcher = runLauncher({
      args: ["--profile", "maria"],
      publishReady: resolveReady,
      signalTarget,
    });
    let apiPort;
    let ownedContainerId;

    try {
      const details = await withTestTimeout(
        Promise.race([
          ready,
          launcher.then(() => {
            throw new Error("real launcher exited before readiness");
          }),
        ]),
        120_000,
      );
      apiPort = Number(new URL(details.apiOrigin).port);
      assert.equal((await fetch(`${details.apiOrigin}/health`)).status, 200);
      assert.equal((await fetch(details.browserUrl)).status, 200);
      assert.equal(
        await pollUntil(() => {
          const added = [...runningFixtureContainerIds()].filter(
            (id) => !baselineContainers.has(id),
          );
          if (added.length !== 1) return false;
          [ownedContainerId] = added;
          return true;
        }, 5_000),
        true,
      );
    } finally {
      signalTarget.emit("SIGINT");
      assert.equal(await launcher, 0);
    }

    assert.equal(
      await pollUntil(async () => !(await isPortOpen(4174)), 15_000),
      true,
    );
    assert.equal(
      await pollUntil(async () => !(await isPortOpen(apiPort)), 15_000),
      true,
    );
    assert.equal(
      await pollUntil(
        () => !runningFixtureContainerIds().has(ownedContainerId),
        30_000,
      ),
      true,
    );
  },
);
