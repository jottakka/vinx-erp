import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import type {
  BootstrapSnapshot,
  EffectiveCapability,
  MockSessionGateway,
} from "../auth/session-contract";
import { SessionProvider } from "../auth/session-provider";
import { type AtendimentoCard, atendimentoClient } from "./atendimento-client";
import { AtendimentoPage } from "./atendimento-page";

const alfaCompanyId = "11111111-1111-4111-8111-111111111111";
const betaCompanyId = "22222222-2222-4222-8222-222222222222";

const alfaCard: AtendimentoCard = {
  attachmentFileId: null,
  assigneeMembershipId: null,
  column: "novo",
  companyId: alfaCompanyId,
  description: "Visível somente para Alfa",
  id: "33333333-3333-4333-8333-333333333333",
  participantMembershipIds: [],
  title: "Cartão Alfa",
  version: 1,
};
const betaCard: AtendimentoCard = {
  attachmentFileId: null,
  assigneeMembershipId: null,
  column: "novo",
  companyId: betaCompanyId,
  description: "Não pode aparecer na fila Alfa",
  id: "44444444-4444-4444-8444-844444444444",
  participantMembershipIds: [],
  title: "Cartão Beta",
  version: 1,
};

const readCapability = {
  name: "service_desk.ticket.read_queue",
  scopes: ["Company"],
} as const;
const createCapability = {
  name: "service_desk.ticket.create",
  scopes: ["Company"],
} as const;
const moveCapability = {
  name: "service_desk.ticket.move",
  scopes: ["Company"],
} as const;
const assignCapability = {
  name: "service_desk.ticket.assign",
  scopes: ["Company"],
} as const;
const allAtendimentoCapabilities = [
  readCapability,
  createCapability,
  moveCapability,
  assignCapability,
];

function snapshot(
  capabilities: readonly EffectiveCapability[] = allAtendimentoCapabilities,
): BootstrapSnapshot {
  return {
    companies: [
      {
        capabilities,
        companyId: alfaCompanyId,
        companyName: "Alfa",
        membershipId: "55555555-5555-4555-8555-555555555555",
      },
      {
        capabilities,
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
}

function gatewayFor(getBootstrap = async () => snapshot()): MockSessionGateway {
  return {
    getBootstrap,
    login: async () => snapshot(),
    logout: async () => {},
  };
}

function renderPage(bootstrap = snapshot()) {
  render(
    <VinxThemeProvider>
      <SessionProvider gateway={gatewayFor(async () => bootstrap)}>
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
beforeEach(() =>
  vi.spyOn(atendimentoClient, "assignmentOptions").mockResolvedValue([]),
);

describe("AtendimentoPage", () => {
  it("aggregates every authorized company, labels each card, and renders the four real state columns", async () => {
    vi.spyOn(atendimentoClient, "list")
      .mockResolvedValueOnce([alfaCard])
      .mockResolvedValueOnce([{ ...betaCard, column: "cancelado" }]);

    renderPage();

    expect(await screen.findByText(alfaCard.title)).toBeVisible();
    await waitFor(() =>
      expect(atendimentoClient.list).toHaveBeenCalledTimes(2),
    );
    expect(await screen.findByText(betaCard.title)).toBeVisible();
    expect(
      screen.getByRole("combobox", { name: "Filtrar empresas" }),
    ).toHaveTextContent("Todas as empresas autorizadas");
    expect(atendimentoClient.list).toHaveBeenCalledWith(alfaCompanyId);
    expect(atendimentoClient.list).toHaveBeenCalledWith(betaCompanyId);
    expect(screen.getAllByText("Alfa").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Beta").length).toBeGreaterThan(0);
    expect(
      screen.getAllByRole("group", { name: "Empresa Alfa" }).length,
    ).toBeGreaterThan(1);
    for (const label of ["Novos", "Em andamento", "Concluídos", "Cancelados"]) {
      expect(screen.getByRole("heading", { name: label })).toBeVisible();
    }
    expect(document.querySelectorAll("[data-state-tone]")).toHaveLength(4);
  });

  it("filters the board to one authorized company without changing the creation company selector", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "list").mockImplementation(async (companyId) =>
      companyId === alfaCompanyId ? [alfaCard] : [betaCard],
    );
    renderPage();
    expect(await screen.findByText(alfaCard.title)).toBeVisible();
    await user.click(
      screen.getByRole("combobox", { name: "Filtrar empresas" }),
    );
    await user.click(screen.getByRole("option", { name: "Beta" }));
    await user.keyboard("{Escape}");
    await waitFor(() =>
      expect(atendimentoClient.list).toHaveBeenLastCalledWith(betaCompanyId),
    );
    expect(await screen.findByText(betaCard.title)).toBeVisible();
    expect(screen.queryByText(alfaCard.title)).not.toBeInTheDocument();
    expect(
      screen.getByRole("combobox", { name: "Empresa para novo cartão" }),
    ).toBeVisible();
  });

  it("waits for all authorized company reads before replacing the board", async () => {
    const betaCards = deferred<AtendimentoCard[] | null>();
    vi.spyOn(atendimentoClient, "list")
      .mockResolvedValueOnce([alfaCard])
      .mockReturnValueOnce(betaCards.promise);

    renderPage();

    await waitFor(() =>
      expect(atendimentoClient.list).toHaveBeenCalledTimes(2),
    );
    expect(screen.getByRole("button", { name: "Novo cartão" })).toBeDisabled();
    expect(screen.queryByText(alfaCard.title)).not.toBeInTheDocument();
    expect(screen.queryByText(betaCard.title)).not.toBeInTheDocument();
    betaCards.resolve([betaCard]);
    expect(await screen.findByText(alfaCard.title)).toBeVisible();
    expect(await screen.findByText(betaCard.title)).toBeVisible();
  });

  it("creates a card through the modal and closes it", async () => {
    const user = userEvent.setup();
    const created = {
      ...alfaCard,
      description: "Descrição do cartão",
      title: "Novo cartão",
    };
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([]);
    const create = vi.spyOn(atendimentoClient, "create").mockResolvedValue({
      card: created,
      ok: true,
    } as Awaited<ReturnType<typeof atendimentoClient.create>>);

    renderPage();

    await user.click(
      await screen.findByRole("button", { name: "Novo cartão" }),
    );
    await user.type(screen.getByLabelText("Título"), "Novo cartão");
    await user.type(screen.getByLabelText("Descrição"), "Descrição do cartão");
    await user.click(screen.getByRole("button", { name: "Criar cartão" }));

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    expect(create).toHaveBeenCalledWith(
      alfaCompanyId,
      "Novo cartão",
      "Descrição do cartão",
    );
    await waitFor(() =>
      expect(screen.getAllByText("Novo cartão").length).toBeGreaterThanOrEqual(
        1,
      ),
    );
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Novo cartão" })).toBeVisible(),
    );
  });

  it("keeps an optional document visibly selected in the create dialog", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([]);

    renderPage();

    await user.click(
      await screen.findByRole("button", { name: "Novo cartão" }),
    );
    await user.upload(
      screen.getByTestId("atendimento-attachment-picker"),
      new File(["documento"], "evidencia.pdf", { type: "application/pdf" }),
    );

    expect(screen.getByRole("button", { name: "evidencia.pdf" })).toBeVisible();
  });

  it("offers only Visualizar and no create button without create permission", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard]);

    renderPage(snapshot([readCapability, moveCapability, assignCapability]));

    expect(await screen.findByText(alfaCard.title)).toBeVisible();
    expect(screen.queryByRole("button", { name: "Novo cartão" })).toBeNull();

    await user.click(screen.getByRole("button", { name: "Visualizar" }));
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toBeVisible();
    expect(within(dialog).getByText(alfaCard.description)).toBeVisible();
  });

  it("moves a card to another column by calling the move client", async () => {
    const user = userEvent.setup();
    const moved = { ...alfaCard, column: "em_andamento" as const };
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard]);
    const move = vi.spyOn(atendimentoClient, "move").mockResolvedValue({
      card: moved,
      ok: true,
    } as Awaited<ReturnType<typeof atendimentoClient.move>>);

    renderPage();

    await user.click(
      await screen.findByRole("button", { name: "Mover para Em andamento" }),
    );
    expect(
      screen.queryByRole("button", { name: "Mover para Cancelados" }),
    ).not.toBeInTheDocument();

    await waitFor(() => expect(move).toHaveBeenCalledTimes(1));
    expect(move.mock.calls[0]?.[1]).toMatchObject({
      id: alfaCard.id,
      column: "novo",
    });
    expect(move.mock.calls[0]?.[2]).toBe("em_andamento");
  });

  it("uses named same-company assignment options and never exposes membership IDs", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard]);
    vi.spyOn(atendimentoClient, "assignmentOptions").mockResolvedValue([
      {
        displayName: "Maria Silva",
        membershipId: "88888888-8888-4888-8888-888888888888",
      },
    ]);
    const assign = vi.spyOn(atendimentoClient, "assign").mockResolvedValue({
      card: alfaCard,
      ok: true,
    } as Awaited<ReturnType<typeof atendimentoClient.assign>>);

    renderPage();

    await user.click(await screen.findByRole("button", { name: "Atribuir" }));
    await user.click(
      await screen.findByRole("combobox", { name: "Responsável" }),
    );
    await user.click(
      await screen.findByRole("option", { name: "Maria Silva" }),
    );
    await user.click(
      screen.getByRole("button", { name: "Confirmar atribuição" }),
    );

    await waitFor(() =>
      expect(assign).toHaveBeenCalledWith(
        alfaCompanyId,
        alfaCard,
        "88888888-8888-4888-8888-888888888888",
      ),
    );
    expect(
      screen.queryByText("88888888-8888-4888-8888-888888888888"),
    ).not.toBeInTheDocument();
  });

  it("cancels through the dedicated terminal command", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard]);
    const cancel = vi.spyOn(atendimentoClient, "cancel").mockResolvedValue({
      card: { ...alfaCard, column: "cancelado" },
      ok: true,
    } as Awaited<ReturnType<typeof atendimentoClient.cancel>>);

    renderPage();

    await user.click(
      await screen.findByRole("button", { name: "Cancelar cartão" }),
    );
    await user.type(
      screen.getByLabelText("Motivo do cancelamento"),
      "Duplicado",
    );
    await user.click(
      screen.getByRole("button", { name: "Confirmar cancelamento" }),
    );

    await waitFor(() =>
      expect(cancel).toHaveBeenCalledWith(alfaCompanyId, alfaCard, "Duplicado"),
    );
    await waitFor(() =>
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
    );
    expect(screen.getByRole("heading", { name: "Cancelados" })).toBeVisible();
  });

  it("blocks a cancellation reason longer than 500 characters", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard]);
    const cancel = vi.spyOn(atendimentoClient, "cancel");
    renderPage();
    await user.click(
      await screen.findByRole("button", { name: "Cancelar cartão" }),
    );
    fireEvent.change(screen.getByLabelText("Motivo do cancelamento"), {
      target: { value: "x".repeat(501) },
    });
    expect(screen.getByText("Use no máximo 500 caracteres.")).toBeVisible();
    expect(
      screen.getByRole("button", { name: "Confirmar cancelamento" }),
    ).toBeDisabled();
    expect(cancel).not.toHaveBeenCalled();
  });
});
