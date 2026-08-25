import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { PassThrough } from "node:stream";
import test from "node:test";

import {
  parseLauncherArgs,
  parseReadyOrigin,
  runLauncher,
} from "./run-real-e2e.mjs";

function createFakeChild(label, pid) {
  const child = new EventEmitter();
  child.label = label;
  child.pid = pid;
  child.stdout = new PassThrough();
  child.exitCode = null;
  child.signalCode = null;
  child.finish = (code = 0, signal = null) => {
    child.exitCode = code;
    child.signalCode = signal;
    child.stdout.end();
    child.emit("exit", code, signal);
  };
  return child;
}

function createHarness(overrides = {}) {
  const host = createFakeChild("host", 101);
  const vite = createFakeChild("vite", 202);
  const events = [];
  const spawns = [];
  const output = [];
  let resolveShutdown;
  const shutdown = new Promise((resolve) => {
    resolveShutdown = resolve;
  });

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

      if (child === host) {
        queueMicrotask(() =>
          host.stdout.write("E2E_API_ORIGIN=http://127.0.0.1:54321\n"),
        );
      }

      return child;
    },
    waitForHttp: async (url) => {
      events.push(`http:${url}`);
      setImmediate(() => resolveShutdown("SIGINT"));
    },
    waitForShutdownSignal: () => shutdown,
    killProcessGroup: (child, signal) => {
      events.push(`kill:${child.label}:${signal}`);
      setTimeout(() => {
        events.push(`exit:${child.label}`);
        child.finish(null, signal);
      }, 1);
    },
    writeLine: (line) => output.push(line),
    ...overrides,
  };

  return { deps, events, host, output, spawns, vite };
}

test("parseLauncherArgs accepts each allow-listed profile", () => {
  for (const profile of ["maria", "beatriz", "joao", "clara"]) {
    assert.deepEqual(parseLauncherArgs(["--profile", profile]), { profile });
  }
});

test("parseLauncherArgs rejects missing, extra, duplicate, and unknown arguments", () => {
  for (const args of [
    [],
    ["--profile"],
    ["--profile", "maria", "extra"],
    ["--profile", "maria", "--profile", "clara"],
    ["--profile", "desconhecido"],
  ]) {
    assert.throws(
      () => parseLauncherArgs(args),
      /E2E launcher failed: invalid_arguments/,
    );
  }
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

test("runLauncher does not start Vite when the host exits before readiness", async () => {
  const harness = createHarness({
    spawnProcess: (command, args, options) => {
      harness.spawns.push({ command, args, options });
      queueMicrotask(() => harness.host.finish(1));
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
      queueMicrotask(() =>
        harness.host.stdout.write(
          "E2E_API_ORIGIN=http://example.test:54321\n",
        ),
      );
      return harness.host;
    },
  });

  await assert.rejects(
    runLauncher(harness.deps),
    /E2E launcher failed: invalid_host_readiness/,
  );
  assert.equal(harness.spawns.length, 1);
});

test("runLauncher terminates and awaits the host group after readiness timeout", async () => {
  const harness = createHarness({
    readinessTimeoutMs: 5,
    spawnProcess: (command, args, options) => {
      harness.spawns.push({ command, args, options });
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

test("runLauncher forwards a signal received during host startup", async () => {
  const harness = createHarness({
    readinessTimeoutMs: 50,
    spawnProcess: (command, args, options) => {
      harness.spawns.push({ command, args, options });
      return harness.host;
    },
    waitForShutdownSignal: async () => "SIGINT",
  });

  await runLauncher(harness.deps);

  assert.equal(harness.spawns.length, 1);
  assert.deepEqual(
    harness.events.filter((event) => event.startsWith("kill:") || event.startsWith("exit:")),
    ["kill:host:SIGINT", "exit:host"],
  );
});

test("runLauncher starts exact child commands and shuts Vite down before host", async () => {
  const harness = createHarness();

  await runLauncher(harness.deps);

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
      stdio: ["ignore", "pipe", "ignore"],
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
