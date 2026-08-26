import {
  act,
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
import type { AtendimentoAttachmentUploader } from "./atendimento-create-dialog";
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

function renderPage(
  bootstrap = snapshot(),
  attachmentUploader?: AtendimentoAttachmentUploader,
) {
  render(
    <VinxThemeProvider>
      <SessionProvider gateway={gatewayFor(async () => bootstrap)}>
        <AtendimentoPage attachmentUploader={attachmentUploader} />
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
    ).toBeGreaterThan(0);
    for (const label of ["Novos", "Em andamento", "Concluídos", "Cancelados"]) {
      expect(screen.getByRole("heading", { name: label })).toBeVisible();
    }
    expect(document.querySelectorAll("[data-state-tone]")).toHaveLength(4);
  });

  it("filters the board without exposing the create-company choice on the page", async () => {
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
      screen.queryByRole("combobox", { name: "Empresa do atendimento" }),
    ).not.toBeInTheDocument();
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
    expect(
      screen.getByRole("button", { name: "Novo atendimento" }),
    ).toBeEnabled();
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

    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Novo atendimento" }),
      ).toBeEnabled(),
    );
    await user.click(screen.getByRole("button", { name: "Novo atendimento" }));
    await user.type(screen.getByLabelText("Título"), "Novo cartão");
    await user.type(screen.getByLabelText("Descrição"), "Descrição do cartão");
    await user.click(screen.getByRole("button", { name: "Criar cartão" }));

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    expect(create).toHaveBeenCalledWith(
      alfaCompanyId,
      expect.stringMatching(
        /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
      ),
      "Novo cartão",
      "Descrição do cartão",
      { attachmentFileId: null, participantMembershipIds: [] },
    );
    await waitFor(() =>
      expect(screen.getAllByText("Novo cartão").length).toBeGreaterThanOrEqual(
        1,
      ),
    );
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Novo atendimento" }),
      ).toBeVisible(),
    );
  });

  it("moves focus from Novo atendimento into the create dialog", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([]);

    renderPage();

    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Novo atendimento" }),
      ).toBeEnabled(),
    );
    const createButton = screen.getByRole("button", {
      name: "Novo atendimento",
    });
    await user.click(createButton);

    expect(await screen.findByLabelText("Título")).toHaveFocus();
    expect(createButton).not.toHaveFocus();
  });

  it("keeps the create dialog data after a real client creation failure", async () => {
    const user = userEvent.setup();
    const attachmentFileId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([]);
    vi.spyOn(atendimentoClient, "assignmentOptions").mockResolvedValue([
      {
        displayName: "Maria Silva",
        membershipId: "88888888-8888-4888-8888-888888888888",
      },
    ]);
    const create = vi.spyOn(atendimentoClient, "create").mockResolvedValue({
      code: "atendimento_unavailable",
      ok: false,
    } as Awaited<ReturnType<typeof atendimentoClient.create>>);
    const uploader: AtendimentoAttachmentUploader = {
      upload: vi.fn().mockResolvedValue({
        attachmentFileId,
        ok: true,
      }),
    };

    renderPage(snapshot(), uploader);

    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Novo atendimento" }),
      ).toBeEnabled(),
    );
    await user.click(screen.getByRole("button", { name: "Novo atendimento" }));
    await user.type(screen.getByLabelText("Título"), "Falha preservada");
    await user.type(screen.getByLabelText("Descrição"), "Tente novamente");
    await user.click(
      screen.getByRole("combobox", { name: "Pessoas envolvidas" }),
    );
    await user.click(
      await screen.findByRole("option", { name: "Maria Silva" }),
    );
    await user.keyboard("{Escape}");
    await user.upload(
      screen.getByTestId("atendimento-attachment-picker"),
      new File(["documento"], "evidencia.pdf", { type: "application/pdf" }),
    );
    await user.click(screen.getByRole("button", { name: "Criar cartão" }));

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    expect(create).toHaveBeenCalledWith(
      alfaCompanyId,
      expect.stringMatching(
        /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
      ),
      "Falha preservada",
      "Tente novamente",
      {
        attachmentFileId,
        participantMembershipIds: ["88888888-8888-4888-8888-888888888888"],
      },
    );
    expect(screen.getByRole("dialog")).toBeVisible();
    expect(screen.getByLabelText("Título")).toHaveValue("Falha preservada");
    expect(screen.getByLabelText("Descrição")).toHaveValue("Tente novamente");
    expect(screen.getByRole("button", { name: "evidencia.pdf" })).toBeVisible();
    expect(
      screen.getByText("Não foi possível concluir a ação. Tente novamente."),
    ).toBeVisible();
    expect(screen.getByRole("button", { name: "Criar cartão" })).toBeEnabled();
  });

  it("replays an ambiguous attachment create with one upload and the exact same command", async () => {
    const user = userEvent.setup();
    const attachmentFileId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([]);
    const create = vi
      .spyOn(atendimentoClient, "create")
      .mockResolvedValueOnce({
        code: "atendimento_unavailable",
        ok: false,
      } as Awaited<ReturnType<typeof atendimentoClient.create>>)
      .mockResolvedValueOnce({
        card: { ...alfaCard, title: "Pedido idempotente" },
        ok: true,
      } as Awaited<ReturnType<typeof atendimentoClient.create>>);
    const uploader: AtendimentoAttachmentUploader = {
      upload: vi.fn().mockResolvedValue({ attachmentFileId, ok: true }),
    };

    renderPage(snapshot(), uploader);
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Novo atendimento" }),
      ).toBeEnabled(),
    );
    await user.click(screen.getByRole("button", { name: "Novo atendimento" }));
    await user.type(screen.getByLabelText("Título"), "Pedido idempotente");
    await user.type(screen.getByLabelText("Descrição"), "Descrição");
    await user.upload(
      screen.getByTestId("atendimento-attachment-picker"),
      new File(["documento"], "evidencia.pdf", { type: "application/pdf" }),
    );
    await user.click(screen.getByRole("button", { name: "Criar cartão" }));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    const firstCommand = create.mock.calls[0];
    expect(firstCommand?.[1]).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );

    await user.click(screen.getByRole("button", { name: "Criar cartão" }));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(2));
    expect(create.mock.calls[1]).toEqual(firstCommand);
    expect(uploader.upload).toHaveBeenCalledTimes(1);
  });

  it("keeps an optional document visibly selected in the create dialog", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([]);

    renderPage();

    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Novo atendimento" }),
      ).toBeEnabled(),
    );
    await user.click(screen.getByRole("button", { name: "Novo atendimento" }));
    await user.upload(
      screen.getByTestId("atendimento-attachment-picker"),
      new File(["documento"], "evidencia.pdf", { type: "application/pdf" }),
    );

    expect(screen.getByRole("button", { name: "evidencia.pdf" })).toBeVisible();
  });

  it("keeps Novo atendimento disabled with an explanation when no company grants create", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard]);

    renderPage(snapshot([readCapability, moveCapability, assignCapability]));

    expect(await screen.findByText(alfaCard.title)).toBeVisible();
    const createButton = screen.getByRole("button", {
      name: "Novo atendimento",
    });
    expect(createButton).toBeDisabled();
    await user.hover(createButton.parentElement as HTMLElement);
    expect(await screen.findByRole("tooltip")).toHaveTextContent(
      "Sua sessão não pode criar atendimentos.",
    );
    expect(
      screen.queryByRole("combobox", {
        name: "Empresa do atendimento",
      }),
    ).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Visualizar" }));
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toBeVisible();
    expect(within(dialog).getByText(alfaCard.description)).toBeVisible();
  });

  it("enables creation for an exact create-capable company even when it cannot read the queue", async () => {
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([]);
    const readOnlyAlfa = {
      capabilities: [readCapability],
      companyId: alfaCompanyId,
      companyName: "Alfa",
      membershipId: "55555555-5555-4555-8555-555555555555",
    };
    const createCapableBeta = {
      capabilities: [createCapability],
      companyId: betaCompanyId,
      companyName: "Beta",
      membershipId: "66666666-6666-4666-8666-666666666666",
    };

    renderPage({
      companies: [readOnlyAlfa, createCapableBeta],
      flags: {},
      platform: { isPlatformOperator: false },
      user: { displayName: "Ana", id: "77777777-7777-4777-8777-777777777777" },
    });

    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Novo atendimento" }),
      ).toBeEnabled(),
    );
    const createButton = screen.getByRole("button", {
      name: "Novo atendimento",
    });
    expect(atendimentoClient.list).toHaveBeenCalledTimes(1);
    expect(atendimentoClient.list).toHaveBeenCalledWith(alfaCompanyId);
    expect(
      screen.getByRole("combobox", { name: "Filtrar empresas" }),
    ).toHaveTextContent("Todas as empresas autorizadas");
    await userEvent.setup().click(createButton);
    const dialog = await screen.findByRole("dialog");
    expect(
      within(dialog).getByRole("combobox", { name: "Empresa do atendimento" }),
    ).toHaveTextContent("Beta");
    expect(within(dialog).queryByText("Alfa")).toBeNull();
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
      await screen.findByRole("button", { name: "Alterar status" }),
    );
    await user.click(screen.getByRole("menuitem", { name: "Em andamento" }));
    expect(
      screen.queryByRole("menuitem", { name: "Cancelados" }),
    ).not.toBeInTheDocument();

    await waitFor(() => expect(move).toHaveBeenCalledTimes(1));
    expect(move.mock.calls[0]?.[1]).toMatchObject({
      id: alfaCard.id,
      column: "novo",
    });
    expect(move.mock.calls[0]?.[2]).toBe("em_andamento");
  });

  it("ignores a second move for the same card while the first is pending", async () => {
    const user = userEvent.setup();
    const result =
      deferred<Awaited<ReturnType<typeof atendimentoClient.move>>>();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard]);
    const move = vi
      .spyOn(atendimentoClient, "move")
      .mockReturnValue(result.promise);

    renderPage();

    await user.click(
      await screen.findByRole("button", { name: "Alterar status" }),
    );
    await user.click(screen.getByRole("menuitem", { name: "Em andamento" }));
    await waitFor(() => expect(move).toHaveBeenCalledTimes(1));
    expect(
      screen.getByRole("button", { name: "Alterar status" }),
    ).toBeDisabled();

    // Segunda via (arrastar e soltar) enquanto o primeiro move está pendente.
    const card = screen
      .getByText(alfaCard.title)
      .closest(".MuiCard-root") as HTMLElement;
    const targetColumn = screen.getByRole("heading", {
      name: "Em andamento",
    }).parentElement as HTMLElement;
    act(() => {
      fireEvent.dragStart(card);
      fireEvent.drop(targetColumn);
    });

    expect(move).toHaveBeenCalledTimes(1);

    result.resolve({
      card: { ...alfaCard, column: "em_andamento" },
      ok: true,
    } as Awaited<ReturnType<typeof atendimentoClient.move>>);
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Alterar status" }),
      ).toBeEnabled(),
    );
    expect(move).toHaveBeenCalledTimes(1);
  });

  it("anchors each card menu to its trigger and preserves Escape close", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard]);
    renderPage();

    const statusTrigger = await screen.findByRole("button", {
      name: "Alterar status",
    });
    vi.spyOn(statusTrigger, "getBoundingClientRect").mockReturnValue({
      bottom: 138,
      height: 38,
      left: 240,
      right: 320,
      toJSON: () => ({}),
      top: 100,
      width: 80,
      x: 240,
      y: 100,
    });
    await user.click(statusTrigger);
    const statusMenu = await screen.findByRole("menu");
    expect(statusMenu.closest(".MuiMenu-paper")).toHaveStyle({
      left: "240px",
      top: "138px",
    });
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("menu")).toBeNull());

    const actionsTrigger = screen.getByRole("button", { name: "Mais ações" });
    vi.spyOn(actionsTrigger, "getBoundingClientRect").mockReturnValue({
      bottom: 278,
      height: 28,
      left: 420,
      right: 470,
      toJSON: () => ({}),
      top: 250,
      width: 50,
      x: 420,
      y: 250,
    });
    await user.click(actionsTrigger);
    const actionsMenu = await screen.findByRole("menu");
    expect(actionsMenu.closest(".MuiMenu-paper")).toHaveStyle({
      left: "420px",
      top: "278px",
    });
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("menu")).toBeNull());
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

    await user.click(await screen.findByRole("button", { name: "Mais ações" }));
    await user.click(
      screen.getByRole("menuitem", { name: "Atribuir responsável" }),
    );
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

  it("locks assignment controls and suppresses a duplicate confirmation", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard]);
    vi.spyOn(atendimentoClient, "assignmentOptions").mockResolvedValue([
      {
        displayName: "Maria Silva",
        membershipId: "88888888-8888-4888-8888-888888888888",
      },
    ]);
    const result =
      deferred<Awaited<ReturnType<typeof atendimentoClient.assign>>>();
    const assign = vi
      .spyOn(atendimentoClient, "assign")
      .mockReturnValue(result.promise);

    renderPage();

    await user.click(await screen.findByRole("button", { name: "Mais ações" }));
    await user.click(
      screen.getByRole("menuitem", { name: "Atribuir responsável" }),
    );
    await user.click(
      await screen.findByRole("combobox", { name: "Responsável" }),
    );
    await user.click(
      await screen.findByRole("option", { name: "Maria Silva" }),
    );

    const confirm = screen.getByRole("button", {
      name: "Confirmar atribuição",
    });
    act(() => {
      confirm.dispatchEvent(new MouseEvent("click", { bubbles: true }));
      confirm.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });

    expect(assign).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("button", { name: "Atribuindo…" })).toBeDisabled();
    expect(
      screen.getByRole("combobox", { name: "Responsável" }),
    ).toHaveAttribute("aria-disabled", "true");
    expect(screen.getByRole("button", { name: "Cancelar" })).toBeDisabled();
    expect(screen.getByRole("status")).toHaveTextContent(
      "Atribuindo responsável…",
    );

    result.resolve({ card: alfaCard, ok: true });
    await waitFor(() =>
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
    );
  });

  it("cancels through the dedicated terminal command", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard]);
    const cancel = vi.spyOn(atendimentoClient, "cancel").mockResolvedValue({
      card: { ...alfaCard, column: "cancelado" },
      ok: true,
    } as Awaited<ReturnType<typeof atendimentoClient.cancel>>);

    renderPage();

    await user.click(await screen.findByRole("button", { name: "Mais ações" }));
    await user.click(
      screen.getByRole("menuitem", { name: "Cancelar atendimento" }),
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

  it("retries assignment with the current version after an integrity conflict", async () => {
    const user = userEvent.setup();
    const current = { ...alfaCard, version: 2 };
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard]);
    vi.spyOn(atendimentoClient, "assignmentOptions").mockResolvedValue([
      {
        displayName: "Maria Silva",
        membershipId: "88888888-8888-4888-8888-888888888888",
      },
    ]);
    const assign = vi
      .spyOn(atendimentoClient, "assign")
      .mockResolvedValueOnce({ code: "integrity_conflict", current, ok: false })
      .mockResolvedValueOnce({ card: current, ok: true });

    renderPage();
    await user.click(await screen.findByRole("button", { name: "Mais ações" }));
    await user.click(
      screen.getByRole("menuitem", { name: "Atribuir responsável" }),
    );
    await user.click(
      await screen.findByRole("combobox", { name: "Responsável" }),
    );
    await user.click(
      await screen.findByRole("option", { name: "Maria Silva" }),
    );
    await user.click(
      screen.getByRole("button", { name: "Confirmar atribuição" }),
    );
    await waitFor(() => expect(assign).toHaveBeenCalledTimes(1));
    expect(
      await screen.findByText(
        "O cartão foi atualizado enquanto você atuava. Verifique o estado atual da fila.",
      ),
    ).toBeVisible();
    await user.click(
      screen.getByRole("button", { name: "Confirmar atribuição" }),
    );
    await waitFor(() => expect(assign).toHaveBeenCalledTimes(2));

    expect(assign.mock.calls[1]?.[1]).toMatchObject({
      id: alfaCard.id,
      version: 2,
    });
  });

  it("retries cancellation with the current version after an integrity conflict", async () => {
    const user = userEvent.setup();
    const current = { ...alfaCard, version: 2 };
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard]);
    const cancel = vi
      .spyOn(atendimentoClient, "cancel")
      .mockResolvedValueOnce({ code: "integrity_conflict", current, ok: false })
      .mockResolvedValueOnce({
        card: { ...current, column: "cancelado" },
        ok: true,
      });

    renderPage();
    await user.click(await screen.findByRole("button", { name: "Mais ações" }));
    await user.click(
      screen.getByRole("menuitem", { name: "Cancelar atendimento" }),
    );
    await user.type(
      screen.getByLabelText("Motivo do cancelamento"),
      "Duplicado",
    );
    await user.click(
      screen.getByRole("button", { name: "Confirmar cancelamento" }),
    );
    await waitFor(() => expect(cancel).toHaveBeenCalledTimes(1));
    await user.click(
      screen.getByRole("button", { name: "Confirmar cancelamento" }),
    );
    await waitFor(() => expect(cancel).toHaveBeenCalledTimes(2));

    expect(cancel.mock.calls[1]?.[1]).toMatchObject({
      id: alfaCard.id,
      version: 2,
    });
  });

  it("suppresses a duplicate cancellation confirmation while the first request is pending", async () => {
    const user = userEvent.setup();
    const result =
      deferred<Awaited<ReturnType<typeof atendimentoClient.cancel>>>();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard]);
    const cancel = vi
      .spyOn(atendimentoClient, "cancel")
      .mockReturnValue(result.promise);

    renderPage();
    await user.click(await screen.findByRole("button", { name: "Mais ações" }));
    await user.click(
      screen.getByRole("menuitem", { name: "Cancelar atendimento" }),
    );
    await user.type(
      screen.getByLabelText("Motivo do cancelamento"),
      "Duplicado",
    );
    const confirm = screen.getByRole("button", {
      name: "Confirmar cancelamento",
    });
    act(() => {
      confirm.dispatchEvent(new MouseEvent("click", { bubbles: true }));
      confirm.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });

    expect(cancel).toHaveBeenCalledTimes(1);
    result.resolve({ card: { ...alfaCard, column: "cancelado" }, ok: true });
    await waitFor(() =>
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
    );
  });

  it("keeps a created Beta card out of an Alfa-filtered board", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([]);
    vi.spyOn(atendimentoClient, "create").mockResolvedValue({
      card: betaCard,
      ok: true,
    } as Awaited<ReturnType<typeof atendimentoClient.create>>);

    renderPage();
    await waitFor(() =>
      expect(atendimentoClient.list).toHaveBeenCalledTimes(2),
    );
    await user.click(
      screen.getByRole("combobox", { name: "Filtrar empresas" }),
    );
    await user.click(screen.getByRole("option", { name: "Alfa" }));
    await user.keyboard("{Escape}");
    await user.click(screen.getByRole("button", { name: "Novo atendimento" }));
    await user.click(
      screen.getByRole("combobox", { name: "Empresa do atendimento" }),
    );
    await user.click(
      await screen.findByRole("option", { name: "Empresa Beta" }),
    );
    await user.type(screen.getByLabelText("Título"), betaCard.title);
    await user.type(screen.getByLabelText("Descrição"), betaCard.description);
    await user.click(screen.getByRole("button", { name: "Criar cartão" }));

    await waitFor(() =>
      expect(atendimentoClient.create).toHaveBeenCalledTimes(1),
    );
    expect(screen.queryByText(betaCard.title)).not.toBeInTheDocument();
  });

  it("blocks a cancellation reason longer than 500 characters", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "list").mockResolvedValue([alfaCard]);
    const cancel = vi.spyOn(atendimentoClient, "cancel");
    renderPage();
    await user.click(await screen.findByRole("button", { name: "Mais ações" }));
    await user.click(
      screen.getByRole("menuitem", { name: "Cancelar atendimento" }),
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
