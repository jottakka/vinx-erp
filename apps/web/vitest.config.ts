import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react()],
  test: {
    environment: "jsdom",
    exclude: ["e2e/**", "node_modules/**"],
    globals: true,
    // A suíte completa (53 arquivos em forks) sofre com timeouts padrão
    // (5s por teste) sob carga paralela de CI; margem conservadora até a
    // reprodução do flake ser concluída.
    hookTimeout: 15000,
    testTimeout: 15000,
    setupFiles: ["./src/test/setup.ts"],
  },
});
