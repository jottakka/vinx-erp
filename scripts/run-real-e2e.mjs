import { spawn } from "node:child_process";
import { createServer } from "node:net";
import path from "node:path";
import { createInterface } from "node:readline";

const ALLOWED_PROFILES = new Set(["maria", "beatriz", "joao", "clara"]);
const WEB_HOST = "127.0.0.1";
const WEB_PORTS = { clara: 4175, maria: 4174 };
const WEB_PORT = WEB_PORTS.maria;
const BROWSER_URL = `http://${WEB_HOST}:${WEB_PORT}`;
const SINGLE_READY_KEYS = ["E2E_API_ORIGIN", "E2E_SINGLE_READY"];
const SHARED_READY_KEYS = [
  "E2E_API_ORIGIN_MARIA",
  "E2E_API_ORIGIN_CLARA",
  "E2E_DB_HOST",
  "E2E_DB_PORT",
  "E2E_DB_DATABASE",
  "E2E_DB_USER",
  "E2E_DB_PASSWORD_FILE",
  "E2E_SHARED_READY",
];
const HOST_FAILURE_CATEGORIES = new Set([
  "fixture_start_failed",
  "seed_failed",
  "host_start_failed",
  "readback_failed",
  "credential_export_failed",
  "cleanup_failed",
]);

class LauncherError extends Error {
  constructor(category) {
    super(`E2E launcher failed: ${category}`);
    this.category = category;
  }
}

export function parseLauncherArgs(args) {
  if (
    args.length === 2 &&
    args[0] === "--profile" &&
    ALLOWED_PROFILES.has(args[1])
  ) {
    return { mode: "single", profile: args[1] };
  }

  if (args.length === 2 && args[0] === "--profiles" && args[1] === "maria,clara") {
    return { mode: "shared", profiles: ["maria", "clara"] };
  }

  throw new LauncherError("invalid_arguments");
}

export function parseReadyOrigin(line) {
  const match = /^E2E_API_ORIGIN=(http:\/\/127\.0\.0\.1:(\d{1,5}))$/.exec(
    line,
  );
  const port = match ? Number(match[2]) : 0;
  if (!match || port < 1 || port > 65_535) {
    throw new LauncherError("invalid_host_readiness");
  }

  return match[1];
}

export function parseSingleReadiness(lines) {
  if (!Array.isArray(lines) || lines.length !== SINGLE_READY_KEYS.length) {
    throw new LauncherError("invalid_host_readiness");
  }

  for (const [index, line] of lines.entries()) {
    const separator = typeof line === "string" ? line.indexOf("=") : -1;
    if (
      separator <= 0 ||
      line.indexOf("=", separator + 1) !== -1 ||
      line.slice(0, separator) !== SINGLE_READY_KEYS[index]
    ) {
      throw new LauncherError("invalid_host_readiness");
    }
  }

  if (lines[1] !== "E2E_SINGLE_READY=1") {
    throw new LauncherError("invalid_host_readiness");
  }

  return parseReadyOrigin(lines[0]);
}

export function parseSharedReadiness(lines) {
  if (!Array.isArray(lines) || lines.length !== SHARED_READY_KEYS.length) {
    throw new LauncherError("invalid_host_readiness");
  }

  const values = {};
  for (const [index, line] of lines.entries()) {
    const separator = typeof line === "string" ? line.indexOf("=") : -1;
    if (
      separator <= 0 ||
      line.indexOf("=", separator + 1) !== -1 ||
      line.slice(0, separator) !== SHARED_READY_KEYS[index]
    ) {
      throw new LauncherError("invalid_host_readiness");
    }
    values[SHARED_READY_KEYS[index]] = line.slice(separator + 1);
  }

  const mariaApiOrigin = parseReadyOrigin(
    `E2E_API_ORIGIN=${values.E2E_API_ORIGIN_MARIA}`,
  );
  const claraApiOrigin = parseReadyOrigin(
    `E2E_API_ORIGIN=${values.E2E_API_ORIGIN_CLARA}`,
  );
  const databasePort = Number(values.E2E_DB_PORT);
  if (
    !["127.0.0.1", "localhost"].includes(values.E2E_DB_HOST) ||
    !Number.isInteger(databasePort) ||
    databasePort < 1 ||
    databasePort > 65_535 ||
    !/^vinx_fixture_[0-9a-f]+$/.test(values.E2E_DB_DATABASE) ||
    values.E2E_DB_USER !== "postgres" ||
    !path.isAbsolute(values.E2E_DB_PASSWORD_FILE) ||
    /[\0\r\n]/.test(values.E2E_DB_PASSWORD_FILE) ||
    values.E2E_SHARED_READY !== "1"
  ) {
    throw new LauncherError("invalid_host_readiness");
  }

  return {
    mariaApiOrigin,
    claraApiOrigin,
    databaseHost: values.E2E_DB_HOST,
    databasePort,
    databaseName: values.E2E_DB_DATABASE,
    databaseUser: values.E2E_DB_USER,
    databasePasswordFilePath: values.E2E_DB_PASSWORD_FILE,
  };
}

function normalizeError(error, category) {
  return error instanceof LauncherError ? error : new LauncherError(category);
}

function repositoryRoot() {
  const scriptPath = decodeURIComponent(new URL(import.meta.url).pathname);
  return path.resolve(path.dirname(scriptPath), "..");
}

function checkPortAvailable(host, port) {
  return new Promise((resolve, reject) => {
    const server = createServer();
    const finish = (error) => {
      server.removeAllListeners();
      if (error) reject(error);
      else resolve();
    };

    server.unref();
    server.once("error", finish);
    server.listen({ exclusive: true, host, port }, () => server.close(finish));
  });
}

function waitForReadinessBlock(stream, timeoutMs, keys, parseBlock) {
  return new Promise((resolve, reject) => {
    const reader = createInterface({ crlfDelay: Infinity, input: stream });
    const lines = [];
    let done = false;
    const finish = (error, readiness) => {
      if (done) return;
      done = true;
      clearTimeout(timeout);
      reader.off("close", onClose);
      reader.off("line", onLine);
      reader.close();
      if (error) {
        reject(error);
      } else {
        stream.resume();
        resolve(readiness);
      }
    };
    const onClose = () => finish(new LauncherError("host_exit"));
    const onLine = (line) => {
      if (lines.length === 0 && !line.startsWith(`${keys[0]}=`)) {
        if (line.startsWith("E2E_"))
          finish(new LauncherError("invalid_host_readiness"));
        return;
      }
      const expectedKey = keys[lines.length];
      if (!expectedKey || !line.startsWith(`${expectedKey}=`)) {
        finish(new LauncherError("invalid_host_readiness"));
        return;
      }

      lines.push(line);
      if (lines.length !== keys.length) return;
      try {
        finish(undefined, parseBlock(lines));
      } catch (error) {
        finish(normalizeError(error, "invalid_host_readiness"));
      }
    };
    const timeout = setTimeout(
      () => finish(new LauncherError("host_readiness_timeout")),
      timeoutMs,
    );

    reader.once("close", onClose);
    reader.on("line", onLine);
  });
}

function waitForSingleReadiness(stream, timeoutMs) {
  return waitForReadinessBlock(
    stream,
    timeoutMs,
    SINGLE_READY_KEYS,
    parseSingleReadiness,
  );
}

function waitForSharedReadiness(stream, timeoutMs) {
  return waitForReadinessBlock(
    stream,
    timeoutMs,
    SHARED_READY_KEYS,
    parseSharedReadiness,
  );
}

function waitForHostFailure(stream) {
  return new Promise((resolve) => {
    const reader = createInterface({ crlfDelay: Infinity, input: stream });
    let failure;
    reader.on("line", (line) => {
      const match = /^E2E_HOST_FAILED=([a-z_]+)$/.exec(line);
      if (match && HOST_FAILURE_CATEGORIES.has(match[1]))
        failure ??= {
          error: new LauncherError(`host_${match[1]}`),
          kind: "failure",
        };
    });
    reader.once("close", () => resolve(failure));
  });
}

async function hostOutcomeWithDiagnostic(outcome, diagnostic) {
  const event = await outcome;
  if (event.kind !== "failure" || event.error.category !== "host_exit")
    return event;
  return (await diagnostic) ?? event;
}

function readyEventWithHostOutcome(operation, category, hostOutcome) {
  return readyEvent(operation, category).catch(async (error) => {
    if (error instanceof LauncherError && error.category === "host_exit")
      return hostOutcome;
    throw error;
  });
}

function delay(milliseconds, signal) {
  if (signal?.aborted) return Promise.resolve();
  return new Promise((resolve) => {
    const finish = () => {
      clearTimeout(timeout);
      signal?.removeEventListener("abort", finish);
      resolve();
    };
    const timeout = setTimeout(finish, milliseconds);
    signal?.addEventListener("abort", finish, { once: true });
    if (signal?.aborted) finish();
  });
}

async function waitForHttpReady(url, timeoutMs, cancellationSignal) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline && !cancellationSignal?.aborted) {
    const remaining = deadline - Date.now();
    const controller = new AbortController();
    const cancelRequest = () => controller.abort();
    cancellationSignal?.addEventListener("abort", cancelRequest, {
      once: true,
    });
    if (cancellationSignal?.aborted) cancelRequest();
    const timeout = setTimeout(
      () => controller.abort(),
      Math.min(1_000, remaining),
    );
    try {
      const response = await fetch(url, { signal: controller.signal });
      if (response.ok) return;
    } catch {
      // Connection failures are expected until Vite is ready.
    } finally {
      clearTimeout(timeout);
      cancellationSignal?.removeEventListener("abort", cancelRequest);
    }
    await delay(
      Math.min(100, Math.max(0, deadline - Date.now())),
      cancellationSignal,
    );
  }

  if (cancellationSignal?.aborted) {
    throw new LauncherError("vite_readiness_cancelled");
  }
  throw new LauncherError("vite_readiness_timeout");
}

function createSupervisor(child, spawnCategory, exitCategory, requireCleanExit = false) {
  let resolveSpawned;
  let rejectSpawned;
  let resolveOutcome;
  const supervisor = {
    child,
    error: undefined,
    exitCode: undefined,
    exitSignal: undefined,
    finished: false,
    outcome: new Promise((resolve) => {
      resolveOutcome = resolve;
    }),
    spawnCategory,
    spawned: new Promise((resolve, reject) => {
      resolveSpawned = resolve;
      rejectSpawned = reject;
    }),
    started: false,
    requireCleanExit,
  };
  const finish = (category, exitCode, exitSignal) => {
    if (supervisor.finished) return;
    supervisor.finished = true;
    supervisor.exitCode = exitCode;
    supervisor.exitSignal = exitSignal;
    supervisor.error = new LauncherError(category);
    if (!supervisor.started) rejectSpawned(supervisor.error);
    resolveOutcome({ error: supervisor.error, kind: "failure" });
  };

  child.once("spawn", () => {
    if (supervisor.finished) return;
    supervisor.started = true;
    resolveSpawned();
  });
  child.once("error", () =>
    finish(supervisor.started ? exitCategory : spawnCategory, undefined, undefined),
  );
  child.once("exit", (code, signal) =>
    finish(supervisor.started ? exitCategory : spawnCategory, code, signal),
  );
  return supervisor;
}

function startSupervised(command, args, options, categories, deps) {
  try {
    return createSupervisor(
      deps.spawnProcess(command, args, options),
      categories.spawn,
      categories.exit,
      categories.requireCleanExit,
    );
  } catch {
    throw new LauncherError(categories.spawn);
  }
}

function confirmSpawn(supervisor, requireStdout = false, requireStderr = false) {
  const { child } = supervisor;
  if (
    !Number.isInteger(child?.pid) ||
    child.pid <= 0 ||
    (requireStdout && !child.stdout) ||
    (requireStderr && !child.stderr)
  ) {
    throw new LauncherError(supervisor.spawnCategory);
  }
}

function createSignalLatch(target) {
  let firstSignal;
  let resolveOutcome;
  const outcome = new Promise((resolve) => {
    resolveOutcome = resolve;
  });
  const receive = (signal) => {
    if (firstSignal) return;
    firstSignal = signal;
    resolveOutcome({ kind: "signal", signal });
  };
  const onInterrupt = () => receive("SIGINT");
  const onTerminate = () => receive("SIGTERM");

  target.on("SIGINT", onInterrupt);
  target.on("SIGTERM", onTerminate);
  return {
    dispose: () => {
      target.off("SIGINT", onInterrupt);
      target.off("SIGTERM", onTerminate);
    },
    outcome,
  };
}

function readyEvent(operation, category) {
  try {
    return Promise.resolve(operation()).then(
      (value) => ({ kind: "ready", value }),
      (error) => Promise.reject(normalizeError(error, category)),
    );
  } catch (error) {
    return Promise.reject(normalizeError(error, category));
  }
}

function failureOrSignal(event) {
  if (event.kind === "failure") throw event.error;
  return event.kind === "signal" ? event.signal : undefined;
}

function startHost(request, deps) {
  const project = path.resolve(
    deps.repositoryRoot,
    "tests/tools/Vinx.Erp.Foundation.E2eHost/Vinx.Erp.Foundation.E2eHost.csproj",
  );
  return startSupervised(
    "dotnet",
    [
      "run",
      "--project",
      project,
      "--no-launch-profile",
      "--",
      ...(request.mode === "shared"
        ? ["--profiles", "maria,clara"]
        : ["--profile", request.profile]),
    ],
    {
      cwd: deps.repositoryRoot,
      detached: deps.platform !== "win32",
      env: deps.environment,
      stdio: ["ignore", "pipe", "pipe"],
    },
    {
      exit: "host_exit",
      spawn: "host_spawn_failed",
      requireCleanExit: true,
    },
    deps,
  );
}

function startVite(apiOrigin, port, deps) {
  const viteCli = path.resolve(
    deps.repositoryRoot,
    "node_modules/vite/bin/vite.js",
  );
  return startSupervised(
    deps.processExecPath,
    [
      viteCli,
      "--host",
      WEB_HOST,
      "--port",
      String(port),
      "--strictPort",
    ],
    {
      cwd: path.resolve(deps.repositoryRoot, "apps/web"),
      detached: deps.platform !== "win32",
      env: { ...deps.environment, VINX_API_ORIGIN: apiOrigin },
      stdio: ["ignore", "ignore", "ignore"],
    },
    { exit: "vite_exit", spawn: "vite_spawn_failed" },
    deps,
  );
}

async function runSingleSupervised(profile, deps, signals, children) {
  children.host = startHost({ mode: "single", profile }, deps);
  const hostFailure = waitForHostFailure(children.host.child.stderr);
  const hostOutcome = hostOutcomeWithDiagnostic(
    children.host.outcome,
    hostFailure,
  );
  let event = await Promise.race([
    children.host.spawned.then(() => ({ kind: "spawned" })),
    signals.outcome,
  ]);
  let signal = failureOrSignal(event);
  if (signal) return signal;
  confirmSpawn(children.host, true, true);

  event = await Promise.race([
    readyEventWithHostOutcome(
      () =>
        waitForSingleReadiness(
          children.host.child.stdout,
          deps.readinessTimeoutMs,
        ),
      "host_readiness_timeout",
      hostOutcome,
    ),
    hostOutcome,
    signals.outcome,
  ]);
  signal = failureOrSignal(event);
  if (signal) return signal;
  const apiOrigin = event.value;

  children.vite = startVite(apiOrigin, WEB_PORT, deps);
  event = await Promise.race([
    children.vite.spawned.then(() => ({ kind: "spawned" })),
    hostOutcome,
    signals.outcome,
  ]);
  signal = failureOrSignal(event);
  if (signal) return signal;
  confirmSpawn(children.vite);

  const readinessCancellation = new AbortController();
  try {
    event = await Promise.race([
      readyEvent(
        () =>
          deps.waitForHttp(
            BROWSER_URL,
            deps.httpTimeoutMs,
            readinessCancellation.signal,
          ),
        "vite_readiness_timeout",
      ),
      hostOutcome,
      children.vite.outcome,
      signals.outcome,
    ]);
  } finally {
    readinessCancellation.abort();
  }
  signal = failureOrSignal(event);
  if (signal) return signal;
  if (children.host.finished) failureOrSignal(await hostOutcome);
  if (children.vite.finished) throw children.vite.error;

  deps.publishReady({ apiOrigin, browserUrl: BROWSER_URL, profile });
  event = await Promise.race([
    hostOutcome,
    children.vite.outcome,
    signals.outcome,
  ]);
  signal = failureOrSignal(event);
  return signal;
}

async function runSharedSupervised(deps, signals, children) {
  children.host = startHost({ mode: "shared" }, deps);
  const hostFailure = waitForHostFailure(children.host.child.stderr);
  const hostOutcome = hostOutcomeWithDiagnostic(
    children.host.outcome,
    hostFailure,
  );
  let event = await Promise.race([
    children.host.spawned.then(() => ({ kind: "spawned" })),
    signals.outcome,
  ]);
  let signal = failureOrSignal(event);
  if (signal) return signal;
  confirmSpawn(children.host, true, true);

  event = await Promise.race([
    readyEventWithHostOutcome(
      () => waitForSharedReadiness(children.host.child.stdout, deps.readinessTimeoutMs),
      "host_readiness_timeout",
      hostOutcome,
    ),
    hostOutcome,
    signals.outcome,
  ]);
  signal = failureOrSignal(event);
  if (signal) return signal;
  const readiness = event.value;

  children.mariaVite = startVite(readiness.mariaApiOrigin, WEB_PORTS.maria, deps);
  event = await Promise.race([
    children.mariaVite.spawned.then(() => ({ kind: "spawned" })),
    hostOutcome,
    signals.outcome,
  ]);
  signal = failureOrSignal(event);
  if (signal) return signal;
  confirmSpawn(children.mariaVite);

  children.claraVite = startVite(readiness.claraApiOrigin, WEB_PORTS.clara, deps);
  event = await Promise.race([
    children.claraVite.spawned.then(() => ({ kind: "spawned" })),
    hostOutcome,
    children.mariaVite.outcome,
    signals.outcome,
  ]);
  signal = failureOrSignal(event);
  if (signal) return signal;
  confirmSpawn(children.claraVite);

  const readinessCancellation = new AbortController();
  try {
    event = await Promise.race([
      readyEvent(
        () =>
          Promise.all([
            deps.waitForHttp(
              `http://${WEB_HOST}:${WEB_PORTS.maria}`,
              deps.httpTimeoutMs,
              readinessCancellation.signal,
            ),
            deps.waitForHttp(
              `http://${WEB_HOST}:${WEB_PORTS.clara}`,
              deps.httpTimeoutMs,
              readinessCancellation.signal,
            ),
          ]),
        "vite_readiness_timeout",
      ),
      hostOutcome,
      children.mariaVite.outcome,
      children.claraVite.outcome,
      signals.outcome,
    ]);
  } finally {
    readinessCancellation.abort();
  }
  signal = failureOrSignal(event);
  if (signal) return signal;
  if (children.host.finished) failureOrSignal(await hostOutcome);
  if (children.mariaVite.finished) throw children.mariaVite.error;
  if (children.claraVite.finished) throw children.claraVite.error;

  deps.publishReady({
    mode: "shared",
    mariaBrowserUrl: `http://${WEB_HOST}:${WEB_PORTS.maria}`,
    claraBrowserUrl: `http://${WEB_HOST}:${WEB_PORTS.clara}`,
    databaseHost: readiness.databaseHost,
    databasePort: readiness.databasePort,
    databaseName: readiness.databaseName,
    databaseUser: readiness.databaseUser,
    databasePasswordFilePath: readiness.databasePasswordFilePath,
  });
  event = await Promise.race([
    hostOutcome,
    children.mariaVite.outcome,
    children.claraVite.outcome,
    signals.outcome,
  ]);
  signal = failureOrSignal(event);
  return signal;
}

function waitForOutcome(supervisor, timeoutMs) {
  if (supervisor.finished) return Promise.resolve(true);
  return new Promise((resolve) => {
    let done = false;
    const finish = (value) => {
      if (done) return;
      done = true;
      clearTimeout(timeout);
      resolve(value);
    };
    const timeout = setTimeout(() => finish(false), timeoutMs);
    supervisor.outcome.then(() => finish(true));
  });
}

async function stopChild(supervisor, signal, deps) {
  if (!Number.isInteger(supervisor?.child?.pid)) return;
  // A launcher-initiated shutdown is clean when the child exited with code 0
  // or died from exactly the signal we forwarded to its process group: the
  // `dotnet run` wrapper may legitimately terminate by that signal even when
  // the underlying host shut down cleanly. Any other outcome is a cleanup
  // failure, and `undefined` keeps the fully strict check for SIGKILL
  // escalation outcomes.
  const requireCleanExit = (forwardedSignal) => {
    if (
      supervisor.requireCleanExit &&
      (forwardedSignal === undefined ||
        (supervisor.exitCode !== 0 &&
          supervisor.exitSignal !== forwardedSignal))
    ) {
      throw new LauncherError("child_cleanup_failed");
    }
  };
  if (supervisor.finished) return;
  const send = (nextSignal) => {
    try {
      deps.killProcessGroup(supervisor.child, nextSignal, deps.platform);
      return true;
    } catch (error) {
      if (error?.code === "ESRCH") return false;
      throw new LauncherError("child_cleanup_failed");
    }
  };

  if (!send(signal)) {
    // The process group is already gone; the primary failure (if any) is
    // preserved exactly like the already-finished branch above.
    return;
  }
  if (await waitForOutcome(supervisor, deps.shutdownTimeoutMs)) {
    requireCleanExit(signal === "SIGKILL" ? undefined : signal);
    return;
  }
  if (signal === "SIGKILL" || !send("SIGKILL")) {
    throw new LauncherError("child_cleanup_failed");
  }
  if (!(await waitForOutcome(supervisor, 5_000))) {
    throw new LauncherError("child_cleanup_failed");
  }
  requireCleanExit(undefined);
}

async function stopChildren(children, signal, deps) {
  let firstError;
  for (const child of [
    children.claraVite,
    children.mariaVite,
    children.vite,
    children.host,
  ]) {
    try {
      await stopChild(child, signal, deps);
    } catch (error) {
      firstError ??= error;
    }
  }
  if (firstError) throw firstError;
}

function killOwnedProcessGroup(child, signal, platform) {
  process.kill(platform === "win32" ? child.pid : -child.pid, signal);
}

export async function runLauncher(overrides = {}) {
  const deps = {
    args: process.argv.slice(2),
    checkWebPort: checkPortAvailable,
    environment: process.env,
    httpTimeoutMs: 30_000,
    killProcessGroup: killOwnedProcessGroup,
    platform: process.platform,
    processExecPath: process.execPath,
    publishReady: (details) => {
      if (details.mode === "shared") {
        console.log(`E2E_BROWSER_URL_MARIA=${details.mariaBrowserUrl}`);
        console.log("E2E_PROFILE_MARIA=maria");
        console.log(`E2E_BROWSER_URL_CLARA=${details.claraBrowserUrl}`);
        console.log("E2E_PROFILE_CLARA=clara");
        console.log(`E2E_DB_HOST=${details.databaseHost}`);
        console.log(`E2E_DB_PORT=${details.databasePort}`);
        console.log(`E2E_DB_DATABASE=${details.databaseName}`);
        console.log(`E2E_DB_USER=${details.databaseUser}`);
        console.log(`E2E_DB_PASSWORD_FILE=${details.databasePasswordFilePath}`);
        return;
      }

      console.log(`E2E_BROWSER_URL=${details.browserUrl}`);
      console.log(`E2E_PROFILE=${details.profile}`);
    },
    readinessTimeoutMs: 90_000,
    repositoryRoot: repositoryRoot(),
    shutdownTimeoutMs: 30_000,
    signalTarget: process,
    spawnProcess: spawn,
    waitForHttp: waitForHttpReady,
    ...overrides,
  };
  const request = parseLauncherArgs(deps.args);
  try {
    const webPorts = request.mode === "shared"
      ? [WEB_PORTS.maria, WEB_PORTS.clara]
      : [WEB_PORT];
    for (const port of webPorts) {
      await deps.checkWebPort(WEB_HOST, port);
    }
  } catch {
    throw new LauncherError("web_port_unavailable");
  }

  const children = {};
  const signals = createSignalLatch(deps.signalTarget);
  let primaryError;
  let cleanupError;
  let shutdownSignal = "SIGTERM";
  try {
    shutdownSignal = request.mode === "shared"
      ? await runSharedSupervised(deps, signals, children)
      : await runSingleSupervised(request.profile, deps, signals, children);
  } catch (error) {
    primaryError = normalizeError(error, "unexpected_failure");
  }

  try {
    await stopChildren(children, shutdownSignal, deps);
  } catch (error) {
    cleanupError = normalizeError(error, "child_cleanup_failed");
  } finally {
    signals.dispose();
  }

  if (cleanupError) throw cleanupError;
  if (primaryError) throw primaryError;
  return 0;
}

const invokedPath = process.argv[1]
  ? new URL(path.resolve(process.argv[1]), "file:").href
  : undefined;

if (import.meta.url === invokedPath) {
  try {
    process.exitCode = await runLauncher();
  } catch (error) {
    const category =
      error instanceof LauncherError ? error.category : "unexpected_failure";
    console.error(`E2E_LAUNCHER_FAILED=${category}`);
    process.exitCode = category === "invalid_arguments" ? 2 : 1;
  }
}
