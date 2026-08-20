import { describe, expect, it } from "vitest";
import * as routing from "./app-paths";

describe("app route catalog", () => {
  it("defines safe paths and navigation metadata without app-layer dependencies", () => {
    expect((routing as Record<string, unknown>).appRouteCatalog).toEqual([
      { group: "workspace", label: "Início", navigation: true, path: "/" },
      {
        group: "workspace",
        label: "Notificações",
        navigation: true,
        path: "/notificacoes",
      },
      { group: "modules", label: "Paguai", navigation: true, path: "/paguai" },
      {
        group: "modules",
        label: "Atendimento",
        navigation: true,
        path: "/atendimento",
      },
      { group: "modules", label: "Kanban", navigation: true, path: "/kanban" },
      {
        group: "modules",
        label: "Pessoas",
        navigation: true,
        path: "/pessoas",
      },
      {
        group: "support",
        label: "Administração",
        navigation: false,
        path: "/administracao",
      },
      { group: "support", label: "Ajuda", navigation: true, path: "/ajuda" },
    ]);
  });
});
