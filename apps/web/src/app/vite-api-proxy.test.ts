import { once } from "node:events";
import { createServer as createHttpServer } from "node:http";
import type { AddressInfo } from "node:net";
import { resolve } from "node:path";
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

  it("proxies Agent, Foundation, Paguai, Atendimento, and Notifications APIs to the exact loopback origin", () => {
    expect(resolveDevelopmentApiProxy("http://127.0.0.1:5080")).toEqual({
      "^/agent/runs(?:\\?.*)?$": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "^/atendimento/assignment-options(?:\\?|$)": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "^/atendimento/cards(?:/|\\?|$)": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "/foundation": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "/notifications": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "^/paguai/requests(?:/|\\?|$)": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "^/paguai/draft-proposals(?:/|\\?|$)": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
    });
  });

  it("proxies only assignment-options under Atendimento while keeping the SPA route local", () => {
    const proxy = resolveDevelopmentApiProxy("http://127.0.0.1:5080");
    expect(proxy).toBeDefined();
    if (!proxy) {
      throw new Error("Development API proxy was not configured");
    }

    expect(proxy["^/atendimento/assignment-options(?:\\?|$)"]).toEqual({
      target: "http://127.0.0.1:5080",
      changeOrigin: false,
    });
    expect(Object.keys(proxy)).not.toContain("/atendimento");

    for (const context of Object.keys(proxy)) {
      const matches = context.startsWith("^")
        ? new RegExp(context).test("/atendimento")
        : "/atendimento".startsWith(context);
      expect(matches).toBe(false);
    }
  });

  it.each(["/paguai", "/atendimento"])(
    "leaves the module SPA route %s outside every API proxy context",
    (route) => {
      const proxy = resolveDevelopmentApiProxy("http://127.0.0.1:5080");
      expect(proxy).toBeDefined();
      if (!proxy) {
        throw new Error("Development API proxy was not configured");
      }

      for (const context of Object.keys(proxy)) {
        const matches = context.startsWith("^")
          ? new RegExp(context).test(route)
          : route.startsWith(context);
        expect(matches).toBe(false);
      }
    },
  );

  it("proxies API URLs and leaves the dedicated Agent and Notifications SPA routes local", async () => {
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
      // O dev server precisa ancorar no app web, não no cwd: rodando a suíte
      // a partir da raiz do repositório (`vitest --root apps/web`), o cwd é a
      // raiz e o fallback de SPA não encontra index.html, devolvendo 404.
      // O import.meta.url do runner do vitest não é file://, então o caminho
      // vem do estado do teste atual: este arquivo vive em <web>/src/app,
      // logo três níveis acima chegam em <web>, onde está o index.html.
      root: resolve(
        expect.getState().testPath ?? process.cwd(),
        "..",
        "..",
        "..",
      ),
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

      const notifications = await fetch(`${origin}/notifications/inbox`);
      expect(await notifications.text()).toBe("/notifications/inbox");

      const paguai = await fetch(`${origin}/paguai/requests?companyId=test`);
      expect(await paguai.text()).toBe("/paguai/requests?companyId=test");

      const atendimento = await fetch(
        `${origin}/atendimento/cards?companyId=test`,
      );
      expect(await atendimento.text()).toBe(
        "/atendimento/cards?companyId=test",
      );

      // Navegação de browser: o fallback de SPA só atende requisições que
      // aceitam HTML, então o fetch emula o header que o browser enviaria.
      const page = await fetch(`${origin}/agente`, {
        headers: { accept: "text/html" },
      });
      expect(page.headers.get("content-type")).toContain("text/html");
      expect(await page.text()).toContain('<div id="root"></div>');

      const notificationsPage = await fetch(`${origin}/notificacoes`, {
        headers: { accept: "text/html" },
      });
      expect(notificationsPage.headers.get("content-type")).toContain(
        "text/html",
      );
      expect(await notificationsPage.text()).toContain('<div id="root"></div>');
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
      "^/atendimento/assignment-options(?:\\?|$)": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "^/atendimento/cards(?:/|\\?|$)": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "/foundation": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "/notifications": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "^/paguai/requests(?:/|\\?|$)": {
        target: "http://127.0.0.1:5080",
        changeOrigin: false,
      },
      "^/paguai/draft-proposals(?:/|\\?|$)": {
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
