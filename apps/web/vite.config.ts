import react from "@vitejs/plugin-react";
import { defineConfig, type UserConfig } from "vite";

type VinxApiProxy = {
  "^/agent/runs(?:\\?.*)?$": {
    changeOrigin: false;
    target: string;
  };
  "/atendimento": {
    changeOrigin: false;
    target: string;
  };
  "/foundation": {
    changeOrigin: false;
    target: string;
  };
  "/notifications": {
    changeOrigin: false;
    target: string;
  };
  "/paguai": {
    changeOrigin: false;
    target: string;
  };
};

/**
 * Aceita somente o ApiHost local como destino do proxy de desenvolvimento.
 * O browser continua chamando caminhos relativos da própria origem.
 */
export function resolveDevelopmentApiProxy(
  origin = process.env.VINX_API_ORIGIN,
): VinxApiProxy | undefined {
  if (!origin) {
    return undefined;
  }

  try {
    const target = new URL(origin);
    if (
      target.protocol !== "http:" ||
      target.hostname !== "127.0.0.1" ||
      target.username ||
      target.password ||
      target.pathname !== "/" ||
      target.search ||
      target.hash
    ) {
      return undefined;
    }

    return {
      "^/agent/runs(?:\\?.*)?$": {
        changeOrigin: false,
        target: target.origin,
      },
      "/atendimento": {
        changeOrigin: false,
        target: target.origin,
      },
      "/foundation": {
        changeOrigin: false,
        target: target.origin,
      },
      "/notifications": {
        changeOrigin: false,
        target: target.origin,
      },
      "/paguai": {
        changeOrigin: false,
        target: target.origin,
      },
    };
  } catch {
    return undefined;
  }
}

export function createWebViteConfig(
  mode: string,
  origin = process.env.VINX_API_ORIGIN,
): UserConfig {
  const proxy =
    mode === "development" ? resolveDevelopmentApiProxy(origin) : undefined;

  return {
    plugins: [react()],
    ...(proxy ? { server: { proxy } } : {}),
  };
}

// https://vite.dev/config/
export default defineConfig(({ mode }) => createWebViteConfig(mode));
