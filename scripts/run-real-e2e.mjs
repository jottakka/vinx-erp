import { spawn } from "node:child_process";
import { createServer } from "node:net";
import path from "node:path";
import { createInterface } from "node:readline";

const ALLOWED_PROFILES = new Set(["maria", "beatriz", "joao", "clara"]);
const WEB_HOST = "127.0.0.1";
const WEB_PORT = 4174;
const BROWSER_URL = `http://${WEB_HOST}:${WEB_PORT}`;
const DEFAULT_READINESS_TIMEOUT_MS = 90_000;
const DEFAULT_HTTP_TIMEOUT_MS = 30_000;
const DEFAULT_SHUTDOWN_TIMEOUT_MS = 30_000;

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

function resolveRepositoryRoot() {
  const scriptPath = decodeURIComponent(new URL(import.meta.url).pathname);
  return path.resolve(path.dirname(scriptPath), "..");
}

function isOwnedChild(child) {
  return Number.isInteger(child?.pid) && child.pid > 0;
}

function hasExited(child) {
  return child.exitCode !== null || child.signalCode !== null;
}

function normalizeError(error, category) {
  return error instanceof LauncherError ? error : new LauncherError(category);
}

function checkPortAvailable(host, port) {
  return new Promise((resolve, reject) => {
    const server = createServer();
    let settled = false;

    const finish = (callback, value) => {
      if (settled) return;
      settled = true;
      server.removeAllListeners();
      callback(value);
    };

    server.unref();
    server.once("error", (error) => finish(reject, error));
    server.listen({ exclusive: true, host, port }, () => {
      server.close((error) => {
        if (error) finish(reject, error);
        else finish(resolve);
      });
    });
  });
}

function waitForHostOrigin(child, timeoutMs) {
  return new Promise((resolve, reject) => {
    const lines = createInterface({ crlfDelay: Infinity, input: child.stdout });
    let settled = false;

    const cleanup = () => {
      clearTimeout(timeout);
      child.off("error", onError);
      child.off("exit", onExit);
      lines.off("close", onClose);
      lines.off("line", onLine);
      lines.close();
    };
    const succeed = (origin) => {
      if (settled) return;
      settled = true;
      cleanup();
      resolve(origin);
    };
    const fail = (error) => {
      if (settled) return;
      settled = true;
      cleanup();
      reject(error);
    };
    const onError = () => fail(new LauncherError("host_exit"));
    const onExit = () => fail(new LauncherError("host_exit"));
    const onClose = () => fail(new LauncherError("host_exit"));
    const onLine = (line) => {
      if (!line.startsWith("E2E_API_ORIGIN=")) return;

      try {
        succeed(parseReadyOrigin(line));
      } catch (error) {
        fail(normalizeError(error, "invalid_host_readiness"));
      }
    };
    const timeout = setTimeout(
      () => fail(new LauncherError("host_readiness_timeout")),
      timeoutMs,
    );

    child.once("error", onError);
    child.once("exit", onExit);
    lines.once("close", onClose);
    lines.on("line", onLine);
  });
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

async function waitForHttpReady(url, timeoutMs) {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    const remaining = deadline - Date.now();
    const controller = new AbortController();
    const requestTimeout = setTimeout(
      () => controller.abort(),
      Math.min(1_000, remaining),
    );

    try {
      const response = await fetch(url, { signal: controller.signal });
      if (response.ok) return;
    } catch {
      // Readiness is a condition; transient connection failures are expected.
    } finally {
      clearTimeout(requestTimeout);
    }

    await delay(Math.min(100, Math.max(0, deadline - Date.now())));
  }

  throw new LauncherError("vite_readiness_timeout");
}

function waitForVite(child, url, timeoutMs, waitForHttp) {
  return new Promise((resolve, reject) => {
    let settled = false;

    const finish = (callback, value) => {
      if (settled) return;
      settled = true;
      child.off("error", onFailure);
      child.off("exit", onFailure);
      callback(value);
    };
    const onFailure = () =>
      finish(reject, new LauncherError("vite_exit"));

    child.once("error", onFailure);
    child.once("exit", onFailure);
    Promise.resolve(waitForHttp(url, timeoutMs)).then(
      () => finish(resolve),
      (error) =>
        finish(reject, normalizeError(error, "vite_readiness_timeout")),
    );
  });
}

function createShutdownSignalWaiter() {
  let dispose = () => {};
  const promise = new Promise((resolve) => {
    const onInterrupt = () => resolve("SIGINT");
    const onTerminate = () => resolve("SIGTERM");
    dispose = () => {
      process.off("SIGINT", onInterrupt);
      process.off("SIGTERM", onTerminate);
    };
    process.once("SIGINT", onInterrupt);
    process.once("SIGTERM", onTerminate);
  });

  return { dispose, promise };
}

function normalizeSignalWaiter(value) {
  if (value && typeof value === "object" && "promise" in value) return value;
  return { dispose: () => {}, promise: Promise.resolve(value) };
}

function validateShutdownSignal(signal) {
  if (signal !== "SIGINT" && signal !== "SIGTERM") {
    throw new LauncherError("invalid_shutdown_signal");
  }

  return signal;
}

function raceWithShutdown(operation, shutdownPromise) {
  return Promise.race([
    operation.then((value) => ({ kind: "ready", value })),
    shutdownPromise.then((signal) => ({ kind: "shutdown", signal })),
  ]);
}

function killOwnedProcessGroup(child, signal, platform) {
  const target = platform === "win32" ? child.pid : -child.pid;
  process.kill(target, signal);
}

function waitForExit(child, timeoutMs) {
  if (hasExited(child)) return Promise.resolve();

  return new Promise((resolve, reject) => {
    let settled = false;
    const finish = (callback, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      child.off("error", onError);
      child.off("exit", onExit);
      callback(value);
    };
    const onError = () =>
      finish(reject, new LauncherError("child_cleanup_failed"));
    const onExit = () => finish(resolve);
    const timeout = setTimeout(
      () => finish(reject, new LauncherError("child_exit_timeout")),
      timeoutMs,
    );

    child.once("error", onError);
    child.once("exit", onExit);
  });
}

async function signalAndWait(child, signal, deps) {
  if (!isOwnedChild(child) || hasExited(child)) return;

  try {
    deps.killProcessGroup(child, signal, deps.platform);
  } catch (error) {
    if (error?.code === "ESRCH") return;
    throw new LauncherError("child_cleanup_failed");
  }

  await waitForExit(child, deps.shutdownTimeoutMs);
}

async function terminateAndWait(child, signal, deps) {
  if (!isOwnedChild(child) || hasExited(child)) return;

  try {
    await signalAndWait(child, signal, deps);
  } catch (error) {
    if (error?.category !== "child_exit_timeout") {
      throw normalizeError(error, "child_cleanup_failed");
    }

    await signalAndWait(child, "SIGKILL", {
      ...deps,
      shutdownTimeoutMs: 5_000,
    });
  }
}

async function shutdownChildren(vite, host, signal, deps) {
  let firstError;

  for (const child of [vite, host]) {
    try {
      await terminateAndWait(child, signal, deps);
    } catch (error) {
      firstError ??= error;
    }
  }

  if (firstError) throw firstError;
}

function spawnHost(profile, deps) {
  const project = path.resolve(
    deps.repositoryRoot,
    "tests/tools/Vinx.Erp.Foundation.E2eHost/Vinx.Erp.Foundation.E2eHost.csproj",
  );
  const child = deps.spawnProcess(
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
  );

  if (!isOwnedChild(child) || !child.stdout) {
    throw new LauncherError("host_spawn_failed");
  }

  return child;
}

function spawnVite(apiOrigin, deps) {
  const viteCli = path.resolve(
    deps.repositoryRoot,
    "node_modules/vite/bin/vite.js",
  );
  const child = deps.spawnProcess(
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
  );

  if (!isOwnedChild(child)) throw new LauncherError("vite_spawn_failed");
  return child;
}

export async function runLauncher(overrides = {}) {
  const deps = {
    args: process.argv.slice(2),
    checkWebPort: checkPortAvailable,
    environment: process.env,
    httpTimeoutMs: DEFAULT_HTTP_TIMEOUT_MS,
    killProcessGroup: killOwnedProcessGroup,
    platform: process.platform,
    processExecPath: process.execPath,
    readinessTimeoutMs: DEFAULT_READINESS_TIMEOUT_MS,
    repositoryRoot: resolveRepositoryRoot(),
    shutdownTimeoutMs: DEFAULT_SHUTDOWN_TIMEOUT_MS,
    spawnProcess: spawn,
    waitForHttp: waitForHttpReady,
    waitForShutdownSignal: createShutdownSignalWaiter,
    writeLine: (line) => console.log(line),
    ...overrides,
  };
  const { profile } = parseLauncherArgs(deps.args);

  try {
    await deps.checkWebPort(WEB_HOST, WEB_PORT);
  } catch {
    throw new LauncherError("web_port_unavailable");
  }

  const signalWaiter = normalizeSignalWaiter(deps.waitForShutdownSignal());
  let requestedSignal;
  const shutdownPromise = Promise.resolve(signalWaiter.promise).then((signal) => {
    requestedSignal = validateShutdownSignal(signal);
    return requestedSignal;
  });
  let host;
  let vite;
  let shutdownSignal = "SIGTERM";
  let primaryError;

  try {
    host = spawnHost(profile, deps);
    const hostOutcome = await raceWithShutdown(
      waitForHostOrigin(host, deps.readinessTimeoutMs),
      shutdownPromise,
    );

    if (hostOutcome.kind === "shutdown") {
      shutdownSignal = hostOutcome.signal;
    } else {
      vite = spawnVite(hostOutcome.value, deps);
      const viteOutcome = await raceWithShutdown(
        waitForVite(
          vite,
          BROWSER_URL,
          deps.httpTimeoutMs,
          deps.waitForHttp,
        ),
        shutdownPromise,
      );

      if (viteOutcome.kind === "shutdown") {
        shutdownSignal = viteOutcome.signal;
      } else {
        deps.writeLine(`E2E_BROWSER_URL=${BROWSER_URL}`);
        deps.writeLine(`E2E_PROFILE=${profile}`);
        shutdownSignal = requestedSignal ?? (await shutdownPromise);
      }
    }
  } catch (error) {
    primaryError = normalizeError(error, "unexpected_failure");
  } finally {
    signalWaiter.dispose();
  }

  let cleanupError;
  try {
    await shutdownChildren(vite, host, shutdownSignal, deps);
  } catch (error) {
    cleanupError = normalizeError(error, "child_cleanup_failed");
  }

  if (primaryError) throw primaryError;
  if (cleanupError) throw cleanupError;
}

const invokedPath = process.argv[1]
  ? new URL(path.resolve(process.argv[1]), "file:").href
  : undefined;

if (import.meta.url === invokedPath) {
  try {
    await runLauncher();
  } catch (error) {
    const category =
      error instanceof LauncherError ? error.category : "unexpected_failure";
    console.error(`E2E_LAUNCHER_FAILED=${category}`);
    process.exitCode = category === "invalid_arguments" ? 2 : 1;
  }
}
