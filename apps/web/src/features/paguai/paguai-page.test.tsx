import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import type {
  BootstrapSnapshot,
  MockSessionGateway,
} from "../auth/session-contract";
import { SessionProvider } from "../auth/session-provider";
import { PaguaiPage } from "./paguai-page";

const companyId = "11111111-1111-4111-8111-111111111111";
const draft = {
  id: "33333333-3333-4333-8333-333333333333",
  companyId,
  requestId: "22222222-2222-4222-8222-222222222222",
  title: "Papel",
  description: "Papel para impressora",
  state: "rascunho" as const,
  version: 1 as const,
  createdAt: "2026-08-16T12:00:00.000+00:00",
};

const bootstrap: BootstrapSnapshot = {
  companies: [
    {
      membershipId: "44444444-4444-4444-8444-444444444444",
      companyId,
      companyName: "Alfa",
      capabilities: [
        { name: "paguai.draft.read", scopes: ["Company"] },
        { name: "paguai.draft.create", scopes: ["Company"] },
      ],
    },
  ],
  flags: {},
  platform: { isPlatformOperator: false },
  user: {
    id: "55555555-5555-4555-8555-555555555555",
    displayName: "Maria",
  },
};

const gateway: MockSessionGateway = {
  getBootstrap: async () => bootstrap,
  login: async () => bootstrap,
  logout: async () => undefined,
};

describe("PaguaiPage", () => {
  it("renders the selected draft detail with only the MVP metadata", async () => {
    const user = userEvent.setup();
    const draftClient = {
      create: async () => ({ ok: true as const, value: draft }),
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [draft] }),
    };

    render(
      <VinxThemeProvider>
        <SessionProvider gateway={gateway}>
          <PaguaiPage draftClient={draftClient} />
        </SessionProvider>
      </VinxThemeProvider>,
    );

    await user.click(
      await screen.findByRole("button", { name: /Papel.*rascunho/ }),
    );

    expect(
      await screen.findByRole("heading", { name: "Papel", level: 2 }),
    ).toBeVisible();
    expect(screen.getByText("Papel para impressora")).toBeVisible();
    expect(screen.getByText("Estado: rascunho · versão 1")).toBeVisible();
    expect(
      screen.getByText(`Identificador da solicitação: ${draft.requestId}`),
    ).toBeVisible();
  });
});
