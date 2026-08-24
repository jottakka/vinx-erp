import { once } from "node:events";
import { createServer as createHttpServer } from "node:http";
import type { AddressInfo } from "node:net";
import { http, passthrough } from "msw";
import { createServer as createViteServer } from "vite";
import { describe, expect, it } from "vitest";
import {
  createWebViteConfig,
  resolveDevelopmentApiProxy,
} from "../../vite.config";
import { server } from "../test/msw/server";

async function closeHttpServer(server: ReturnType<typeof createHttpServer>) {
  await new Promise<void>((resolve, reject) => {
    server.close((error) => (error ? reject(error) : resolve()));
  });
}

describe("development API proxy", () => {
  it("is absent when no API origin is configured", () => {
    expect(resolveDevelopmentApiProxy()).toBeUndefined();
  });

  it("proxies Agent, Foundation, Paguai, and Atendimento APIs to the exact loopback origin", () => {
    expect(resolveDevelopmentApiProxy("http://127.0.0.1:5080")).toEqual({
      "^/agent/runs(?:\\?.*)?$": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "/atendimento": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "/foundation": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "/paguai": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
    });
  });

  it("proxies only agent run URLs and leaves the dedicated agent SPA route local", async () => {
    const api = createHttpServer((request, response) => {
      response.writeHead(200, { "Content-Type": "text/plain" });
      response.end(request.url);
    });
    api.listen(0, "127.0.0.1");
    await once(api, "listening");
    const apiAddress = api.address() as AddressInfo;
    const config = createWebViteConfig(
      "development",
      `http://127.0.0.1:${apiAddress.port}`,
    );
    const vite = await createViteServer({
      ...config,
      logLevel: "error",
      server: {
        ...(config.server ?? {}),
        host: "127.0.0.1",
        port: 0,
        strictPort: true,
      },
    });

    try {
      await vite.listen();
      const viteAddress = vite.httpServer?.address() as AddressInfo;
      const origin = `http://127.0.0.1:${viteAddress.port}`;
      const apiOrigin = `http://127.0.0.1:${apiAddress.port}`;
      server.use(
        http.all(`${origin}/*`, () => passthrough()),
        http.all(`${apiOrigin}/*`, () => passthrough()),
      );

      const run = await fetch(`${origin}/agent/runs?source=test`);
      expect(await run.text()).toBe("/agent/runs?source=test");

      const page = await fetch(`${origin}/agente`);
      expect(page.headers.get("content-type")).toContain("text/html");
      expect(await page.text()).toContain('<div id="root"></div>');
    } finally {
      await vite.close();
      await closeHttpServer(api);
    }
  });

  it.each([
    "http://localhost:5080",
    "http://192.168.1.10:5080",
    "https://127.0.0.1:5080",
    "http://127.0.0.1:5080/api",
    "http://127.0.0.1:5080?tenant=demo",
    "http://127.0.0.1:5080#api",
    "http://user:password@127.0.0.1:5080",
  ])("rejects unsafe API origin %s", (origin) => {
    expect(resolveDevelopmentApiProxy(origin)).toBeUndefined();
  });
});

describe("web Vite configuration", () => {
  it("wires the API proxy only in development", () => {
    const config = createWebViteConfig("development", "http://127.0.0.1:5080");

    expect(config.server?.proxy).toEqual({
      "^/agent/runs(?:\\?.*)?$": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "/atendimento": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "/foundation": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "/paguai": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
    });
  });

  it("does not configure a preview proxy for production", () => {
    const config = createWebViteConfig("production", "http://127.0.0.1:5080");

    expect(config.server?.proxy).toBeUndefined();
    expect(config.preview?.proxy).toBeUndefined();
  });
});
