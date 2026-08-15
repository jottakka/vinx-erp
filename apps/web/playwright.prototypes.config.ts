import { defineConfig } from "@playwright/test";

const prototypePort = process.env.PLAYWRIGHT_PROTOTYPES_PORT ?? "4174";
const prototypeBaseUrl = `http://127.0.0.1:${prototypePort}`;

export default defineConfig({
  testDir: "./e2e",
  testMatch: "module-prototypes.spec.ts",
  use: {
    baseURL: prototypeBaseUrl,
  },
  webServer: {
    command: `npm run dev -- --host 127.0.0.1 --port ${prototypePort} --strictPort`,
    reuseExistingServer: !process.env.CI,
    url: prototypeBaseUrl,
  },
});
