import { describe, expect, it } from "vitest";
import {
  createWebViteConfig,
  resolveDevelopmentApiProxy,
} from "../../vite.config";

describe("development API proxy", () => {
  it("is absent when no API origin is configured", () => {
    expect(resolveDevelopmentApiProxy()).toBeUndefined();
  });

  it("proxies Foundation, Paguai, and Atendimento APIs to the exact loopback origin", () => {
    expect(resolveDevelopmentApiProxy("http://127.0.0.1:5080")).toEqual({
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
