import { describe, expect, it } from "vitest";
import * as routing from "./app-paths";

describe("app route catalog", () => {
  it("allow-lists an opaque proposal identifier only in the Paguai review query", () => {
    const proposalId = "11111111-1111-4111-8111-111111111111";
    expect(routing.paguaiProposalPath(proposalId)).toBe(
      `/paguai?proposal=${proposalId}`,
    );
    expect(routing.readPaguaiProposalId(`?proposal=${proposalId}`)).toBe(
      proposalId,
    );
    expect(
      routing.readPaguaiProposalId("?proposal=raw-ocr-content"),
    ).toBeNull();
  });

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
      {
        group: "modules",
        label: "Pessoas",
        navigation: true,
        path: "/pessoas",
      },
      {
        group: "modules",
        label: "Permissões",
        navigation: true,
        path: "/permissoes",
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
