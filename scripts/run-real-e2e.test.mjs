import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { EventEmitter } from "node:events";
import { createConnection } from "node:net";
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
    if (child.exitCode !== null || child.signalCode !== null) return;
    child.exitCode = code;
    child.signalCode = signal;
    child.stdout.end();
    child.emit("exit", code, signal);
  };
  return child;
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
  let resolveShutdown;
  const shutdown = new Promise((resolve) => {
    resolveShutdown = resolve;
  });
  const signalWaiter = () => {
    const onInterrupt = () => resolveShutdown("SIGINT");
    const onTerminate = () => resolveShutdown("SIGTERM");
    signalTarget.on("SIGINT", onInterrupt);
    signalTarget.on("SIGTERM", onTerminate);
    return {
      dispose: () => {
        signalTarget.off("SIGINT", onInterrupt);
        signalTarget.off("SIGTERM", onTerminate);
      },
      promise: shutdown,
    };
  };
  let outputLines = 0;
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
            host.stdout.write("E2E_API_ORIGIN=http://127.0.0.1:54321\n")))();
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
    waitForShutdownSignal: signalWaiter,
    killProcessGroup: (child, signal) => {
      events.push(`kill:${child.label}:${signal}`);
      setTimeout(() => {
        events.push(`exit:${child.label}`);
        child.finish(null, signal);
      }, 1);
    },
    publishReady: ({ browserUrl, profile }) =>
      recordReady([
        `E2E_BROWSER_URL=${browserUrl}`,
        `E2E_PROFILE=${profile}`,
      ]),
    writeLine: (line) => {
      output.push(line);
      outputLines += 1;
      if (outputLines === 2) {
        setImmediate(onReady ?? (() => signalTarget.emit("SIGINT")));
      }
    },
    ...dependencyOverrides,
  };

  return { deps, events, host, output, signalTarget, spawns, vite };
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
          "E2E_API_ORIGIN=http://example.test:54321\n",
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
      setTimeout(() => child.finish(null, signal), 10);
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
  delete harness.deps.waitForShutdownSignal;

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
          child.stdout.write("E2E_API_ORIGIN=http://127.0.0.1:54321\n");
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
      if (child !== harness.vite || signal === "SIGKILL") {
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
