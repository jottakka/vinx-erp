import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import type {
  BootstrapSnapshot,
  MockSessionGateway,
} from "../auth/session-contract";
import { SessionProvider } from "../auth/session-provider";
import { type AtendimentoCard, atendimentoClient } from "./atendimento-client";
import { AtendimentoPage } from "./atendimento-page";

const alfaCompanyId = "11111111-1111-4111-8111-111111111111";
const betaCompanyId = "22222222-2222-4222-8222-222222222222";

const alfaCard: AtendimentoCard = {
  assigneeMembershipId: null,
  column: "novo",
  companyId: alfaCompanyId,
  description: "Visível somente para Alfa",
  id: "33333333-3333-4333-8333-333333333333",
  title: "Cartão Alfa",
  version: 1,
};
const betaCard: AtendimentoCard = {
  assigneeMembershipId: null,
  column: "novo",
  companyId: betaCompanyId,
  description: "Não pode aparecer na fila Alfa",
  id: "44444444-4444-4444-8444-844444444444",
  title: "Cartão Beta",
  version: 1,
};

const snapshot: BootstrapSnapshot = {
  companies: [
    {
      capabilities: [],
      companyId: alfaCompanyId,
      companyName: "Alfa",
      membershipId: "55555555-5555-4555-8555-555555555555",
    },
    {
      capabilities: [],
      companyId: betaCompanyId,
      companyName: "Beta",
      membershipId: "66666666-6666-4666-8666-666666666666",
    },
  ],
  flags: {},
  platform: { isPlatformOperator: false },
  user: {
    displayName: "Ana",
    id: "77777777-7777-4777-8777-777777777777",
  },
};

const gateway: MockSessionGateway = {
  getBootstrap: async () => snapshot,
  login: async () => snapshot,
  logout: async () => {},
};

function renderPage() {
  render(
    <VinxThemeProvider>
      <SessionProvider gateway={gateway}>
        <AtendimentoPage />
      </SessionProvider>
    </VinxThemeProvider>,
  );
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  return {
    promise: new Promise<T>((complete) => {
      resolve = complete;
    }),
    resolve,
  };
}

afterEach(() => vi.restoreAllMocks());

describe("AtendimentoPage", () => {
  it("does not render a card outside the explicitly selected company", async () => {
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard, betaCard]);

    renderPage();

    expect(await screen.findByText(alfaCard.title)).toBeVisible();
    expect(screen.queryByText(betaCard.title)).not.toBeInTheDocument();
  });

  it("clears the previous company cards while the next selected company is loading", async () => {
    const user = userEvent.setup();
    const betaCards = deferred<AtendimentoCard[] | null>();
    vi.spyOn(atendimentoClient, "list")
      .mockResolvedValueOnce([alfaCard])
      .mockReturnValueOnce(betaCards.promise);

    renderPage();

    expect(await screen.findByText(alfaCard.title)).toBeVisible();
    await user.click(screen.getByRole("combobox", { name: "Empresa" }));
    await user.click(screen.getByRole("option", { name: "Beta" }));
    await waitFor(() =>
      expect(atendimentoClient.list).toHaveBeenLastCalledWith(betaCompanyId),
    );

    expect(screen.queryByText(alfaCard.title)).not.toBeInTheDocument();

    betaCards.resolve([betaCard]);
    expect(await screen.findByText(betaCard.title)).toBeVisible();
  });
});
