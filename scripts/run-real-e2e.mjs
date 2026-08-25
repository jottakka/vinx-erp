import { spawn } from "node:child_process";
import { createServer } from "node:net";
import path from "node:path";
import { createInterface } from "node:readline";

const ALLOWED_PROFILES = new Set(["maria", "beatriz", "joao", "clara"]);
const WEB_HOST = "127.0.0.1";
const WEB_PORT = 4174;
const BROWSER_URL = `http://${WEB_HOST}:${WEB_PORT}`;

class LauncherError extends Error {
  constructor(category) {
    super(`E2E launcher failed: ${category}`);
    this.category = category;
  }
}

export function parseLauncherArgs(args) {
  if (
    args.length !== 2 ||
    args[0] !== "--profile" ||
    !ALLOWED_PROFILES.has(args[1])
  ) {
    throw new LauncherError("invalid_arguments");
  }

  return { profile: args[1] };
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

function waitForHostOrigin(stream, timeoutMs) {
  return new Promise((resolve, reject) => {
    const lines = createInterface({ crlfDelay: Infinity, input: stream });
    let done = false;
    const finish = (error, origin) => {
      if (done) return;
      done = true;
      clearTimeout(timeout);
      lines.off("close", onClose);
      lines.off("line", onLine);
      lines.close();
      if (error) reject(error);
      else {
        stream.resume();
        resolve(origin);
      }
    };
    const onClose = () => finish(new LauncherError("host_exit"));
    const onLine = (line) => {
      if (!line.startsWith("E2E_API_ORIGIN=")) return;
      try {
        finish(undefined, parseReadyOrigin(line));
      } catch (error) {
        finish(normalizeError(error, "invalid_host_readiness"));
      }
    };
    const timeout = setTimeout(
      () => finish(new LauncherError("host_readiness_timeout")),
      timeoutMs,
    );

    lines.once("close", onClose);
    lines.on("line", onLine);
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

function createSupervisor(child, spawnCategory, exitCategory) {
  let resolveSpawned;
  let rejectSpawned;
  let resolveOutcome;
  const supervisor = {
    child,
    error: undefined,
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
  };
  const finish = (category) => {
    if (supervisor.finished) return;
    supervisor.finished = true;
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
    finish(supervisor.started ? exitCategory : spawnCategory),
  );
  child.once("exit", () =>
    finish(supervisor.started ? exitCategory : spawnCategory),
  );
  return supervisor;
}

function startSupervised(command, args, options, categories, deps) {
  try {
    return createSupervisor(
      deps.spawnProcess(command, args, options),
      categories.spawn,
      categories.exit,
    );
  } catch {
    throw new LauncherError(categories.spawn);
  }
}

function confirmSpawn(supervisor, requireStdout = false) {
  const { child } = supervisor;
  if (
    !Number.isInteger(child?.pid) ||
    child.pid <= 0 ||
    (requireStdout && !child.stdout)
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

function startHost(profile, deps) {
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
      "--profile",
      profile,
    ],
    {
      cwd: deps.repositoryRoot,
      detached: deps.platform !== "win32",
      env: deps.environment,
      stdio: ["ignore", "pipe", "ignore"],
    },
    { exit: "host_exit", spawn: "host_spawn_failed" },
    deps,
  );
}

function startVite(apiOrigin, deps) {
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
      String(WEB_PORT),
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

async function runSupervised(profile, deps, signals, children) {
  children.host = startHost(profile, deps);
  let event = await Promise.race([
    children.host.spawned.then(() => ({ kind: "spawned" })),
    signals.outcome,
  ]);
  let signal = failureOrSignal(event);
  if (signal) return signal;
  confirmSpawn(children.host, true);

  event = await Promise.race([
    readyEvent(
      () =>
        waitForHostOrigin(
          children.host.child.stdout,
          deps.readinessTimeoutMs,
        ),
      "host_readiness_timeout",
    ),
    children.host.outcome,
    signals.outcome,
  ]);
  signal = failureOrSignal(event);
  if (signal) return signal;
  const apiOrigin = event.value;

  children.vite = startVite(apiOrigin, deps);
  event = await Promise.race([
    children.vite.spawned.then(() => ({ kind: "spawned" })),
    children.host.outcome,
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
      children.host.outcome,
      children.vite.outcome,
      signals.outcome,
    ]);
  } finally {
    readinessCancellation.abort();
  }
  signal = failureOrSignal(event);
  if (signal) return signal;
  if (children.host.finished) throw children.host.error;
  if (children.vite.finished) throw children.vite.error;

  deps.publishReady({ apiOrigin, browserUrl: BROWSER_URL, profile });
  event = await Promise.race([
    children.host.outcome,
    children.vite.outcome,
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
  if (supervisor?.finished || !Number.isInteger(supervisor?.child?.pid)) return;
  const send = (nextSignal) => {
    try {
      deps.killProcessGroup(supervisor.child, nextSignal, deps.platform);
      return true;
    } catch (error) {
      if (error?.code === "ESRCH") return false;
      throw new LauncherError("child_cleanup_failed");
    }
  };

  if (!send(signal)) return;
  if (await waitForOutcome(supervisor, deps.shutdownTimeoutMs)) return;
  if (signal === "SIGKILL" || !send("SIGKILL")) {
    throw new LauncherError("child_cleanup_failed");
  }
  if (!(await waitForOutcome(supervisor, 5_000))) {
    throw new LauncherError("child_cleanup_failed");
  }
}

async function stopChildren(children, signal, deps) {
  let firstError;
  for (const child of [children.vite, children.host]) {
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
    publishReady: ({ browserUrl, profile }) => {
      console.log(`E2E_BROWSER_URL=${browserUrl}`);
      console.log(`E2E_PROFILE=${profile}`);
    },
    readinessTimeoutMs: 90_000,
    repositoryRoot: repositoryRoot(),
    shutdownTimeoutMs: 30_000,
    signalTarget: process,
    spawnProcess: spawn,
    waitForHttp: waitForHttpReady,
    ...overrides,
  };
  const { profile } = parseLauncherArgs(deps.args);
  try {
    await deps.checkWebPort(WEB_HOST, WEB_PORT);
  } catch {
    throw new LauncherError("web_port_unavailable");
  }

  const children = {};
  const signals = createSignalLatch(deps.signalTarget);
  let primaryError;
  let cleanupError;
  let shutdownSignal = "SIGTERM";
  try {
    shutdownSignal = await runSupervised(profile, deps, signals, children);
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
