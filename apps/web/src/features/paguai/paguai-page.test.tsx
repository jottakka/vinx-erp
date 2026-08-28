import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import type {
  BootstrapSnapshot,
  MockSessionGateway,
} from "../auth/session-contract";
import { SessionProvider, useSession } from "../auth/session-provider";
import type { CreatePaguaiDraftInput } from "./paguai-draft-client";
import { PaguaiPage } from "./paguai-page";

const companyId = "11111111-1111-4111-8111-111111111111";
const secondCompanyId = "66666666-6666-4666-8666-666666666666";
const draft = {
  id: "33333333-3333-4333-8333-333333333333",
  companyId,
  requestId: "22222222-2222-4222-8222-222222222222",
  title: "Papel",
  description: "Papel para impressora",
  amount: "1250.00",
  currency: "BRL" as const,
  dueDate: "2026-09-01",
  beneficiaryDisplayName: "Fornecedor Demo",
  invoiceFileId: null,
  proofRequirement: "required" as const,
  state: "rascunho" as const,
  version: 1 as const,
  createdAt: "2026-08-16T12:00:00.000+00:00",
};

const readCapability = {
  name: "paguai.draft.read",
  scopes: ["Company"],
} as const;
const createCapability = {
  name: "paguai.draft.create",
  scopes: ["Company"],
} as const;

function bootstrapFor(
  companies: BootstrapSnapshot["companies"],
): BootstrapSnapshot {
  return {
    companies,
    flags: {},
    platform: { isPlatformOperator: false },
    user: { id: "55555555-5555-4555-8555-555555555555", displayName: "Maria" },
  };
}

const bootstrap: BootstrapSnapshot = bootstrapFor([
  {
    membershipId: "44444444-4444-4444-8444-444444444444",
    companyId,
    companyName: "Alfa",
    capabilities: [readCapability, createCapability],
  },
]);

const gateway: MockSessionGateway = {
  getBootstrap: async () => bootstrap,
  login: async () => bootstrap,
  logout: async () => undefined,
};

function renderPage(
  draftClient: unknown,
  sessionGateway: MockSessionGateway = gateway,
) {
  render(
    <VinxThemeProvider>
      <SessionProvider gateway={sessionGateway}>
        <PaguaiPage
          draftClient={
            draftClient as Parameters<typeof PaguaiPage>[0]["draftClient"]
          }
        />
      </SessionProvider>
    </VinxThemeProvider>,
  );
}

function RefreshBootstrapButton() {
  const { bootstrap, refreshBootstrap } = useSession();

  return (
    <button
      disabled={!bootstrap}
      onClick={() => void refreshBootstrap()}
      type="button"
    >
      Atualizar sessão
    </button>
  );
}

function renderPageWithSessionRefresh(
  draftClient: unknown,
  sessionGateway: MockSessionGateway,
) {
  render(
    <VinxThemeProvider>
      <SessionProvider gateway={sessionGateway}>
        <RefreshBootstrapButton />
        <PaguaiPage
          draftClient={
            draftClient as Parameters<typeof PaguaiPage>[0]["draftClient"]
          }
        />
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

async function openCreate(user: ReturnType<typeof userEvent.setup>) {
  await user.click(
    await screen.findByRole("button", { name: "Nova solicitação" }),
  );
  await screen.findByRole("heading", { name: "Nova solicitação" });
}

async function fillForm(user: ReturnType<typeof userEvent.setup>) {
  await user.type(
    screen.getByRole("textbox", { name: "Título" }),
    "Novo papel",
  );
  await user.type(
    screen.getByRole("textbox", { name: "Descrição" }),
    "Descrição do rascunho",
  );
  await user.type(
    screen.getByRole("textbox", { name: "Valor (R$)" }),
    "1.250,00",
  );
  const dueDate = document.querySelector<HTMLInputElement>(
    'input[name="dueDate"]',
  );
  if (!dueDate) throw new Error("due date input missing");
  fireEvent.change(dueDate, { target: { value: "2026-09-01" } });
  await user.type(
    screen.getByRole("textbox", { name: "Quem receberá" }),
    "Fornecedor Demo",
  );
  await user.click(
    screen.getByRole("combobox", { name: "Comprovante de pagamento" }),
  );
  await user.click(await screen.findByRole("option", { name: "Obrigatório" }));
}

describe("PaguaiPage", () => {
  it("lists drafts first and opens the create modal from the header action", async () => {
    const user = userEvent.setup();
    const draftClient = {
      create: async () => ({ ok: true as const, value: draft }),
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [draft] }),
    };

    renderPage(draftClient);

    expect(
      await screen.findByRole("heading", { name: "Papel", level: 3 }),
    ).toBeVisible();
    expect(
      screen.getByText(
        "Acompanhe solicitações financeiras das empresas autorizadas.",
      ),
    ).toBeVisible();
    expect(screen.queryByText(/MVP/)).toBeNull();
    expect(screen.queryByRole("dialog")).toBeNull();

    await openCreate(user);
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toBeVisible();
    expect(within(dialog).getByText("Alfa")).toBeVisible();
    expect(dialog.textContent).not.toContain(companyId);
  });

  it("keeps the native date label above the browser-rendered date value", async () => {
    const user = userEvent.setup();
    const draftClient = {
      create: async () => ({ ok: true as const, value: draft }),
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    renderPage(draftClient);
    await openCreate(user);

    expect(
      screen.getByText("Pagar até", { selector: "label" }),
    ).toHaveAttribute("data-shrink", "true");
    expect(
      screen.getByText("Nome para referência; não confirma dados bancários."),
    ).toBeVisible();
    expect(
      screen.getByText("Será exigido na confirmação do pagamento."),
    ).toBeVisible();
  });

  it("opens the read-only view dialog with the financial fields", async () => {
    const user = userEvent.setup();
    const draftClient = {
      create: async () => ({ ok: true as const, value: draft }),
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [draft] }),
    };

    renderPage(draftClient);

    await user.click(await screen.findByRole("button", { name: "Visualizar" }));

    const dialog = await screen.findByRole("dialog");
    expect(dialog).toBeVisible();
    expect(within(dialog).getByText("Papel para impressora")).toBeVisible();
    expect(
      within(dialog).getByText(
        (_content, element) => element?.textContent === "Estado: Rascunho",
      ),
    ).toBeVisible();
    expect(within(dialog).queryByText(/versão/i)).toBeNull();
    expect(
      within(dialog).getAllByText("R$ 1.250,00").length,
    ).toBeGreaterThanOrEqual(1);
    expect(within(dialog).getByText("Pagar até: 01/09/2026")).toBeVisible();
    expect(
      within(dialog).getByText(/Quem receberá: Fornecedor Demo/),
    ).toBeVisible();
    expect(within(dialog).queryByText(/\(informativo\)/)).toBeNull();
  });

  it("aggregates all authorized companies in deterministic order and identifies each card", async () => {
    const betaDraft = {
      ...draft,
      id: "77777777-7777-4777-8777-777777777777",
      companyId: secondCompanyId,
      title: "Toner",
      createdAt: "2026-08-17T12:00:00.000+00:00",
    };
    const twoCompanyBootstrap = bootstrapFor([
      ...bootstrap.companies,
      {
        membershipId: "77777777-7777-4777-8777-777777777777",
        companyId: secondCompanyId,
        companyName: "Beta",
        capabilities: [readCapability, createCapability],
      },
    ]);
    const twoCompanyGateway: MockSessionGateway = {
      ...gateway,
      getBootstrap: async () => twoCompanyBootstrap,
    };
    const draftClient = {
      create: async () => ({ ok: true as const, value: draft }),
      detail: async () => ({ ok: true as const, value: draft }),
      list: vi.fn(async (id: string) =>
        id === companyId
          ? { ok: true as const, value: [draft] }
          : { ok: true as const, value: [betaDraft] },
      ),
    };

    renderPage(draftClient, twoCompanyGateway);

    expect(
      await screen.findByRole("heading", { name: "Papel", level: 3 }),
    ).toBeVisible();
    expect(
      screen.getByRole("heading", { name: "Toner", level: 3 }),
    ).toBeVisible();
    const cards = screen.getAllByRole("heading", { level: 3 });
    expect(cards.map((item) => item.textContent)).toEqual(["Toner", "Papel"]);
    expect(screen.getByText("Alfa")).toBeVisible();
    expect(screen.getByText("Beta")).toBeVisible();
    expect(screen.getAllByText("Rascunho")).toHaveLength(2);
    expect(
      screen.queryByRole("button", { name: "Nova solicitação" }),
    ).toBeNull();
    expect(
      screen.getByText(
        "Selecione uma única empresa para criar uma solicitação.",
      ),
    ).toBeVisible();
  });

  it("keeps successful companies visible and reports a partial list failure", async () => {
    const twoCompanyBootstrap = bootstrapFor([
      ...bootstrap.companies,
      {
        membershipId: "77777777-7777-4777-8777-777777777777",
        companyId: secondCompanyId,
        companyName: "Beta",
        capabilities: [readCapability, createCapability],
      },
    ]);
    const twoCompanyGateway: MockSessionGateway = {
      ...gateway,
      getBootstrap: async () => twoCompanyBootstrap,
    };
    const draftClient = {
      create: async () => ({ ok: true as const, value: draft }),
      detail: async () => ({ ok: true as const, value: draft }),
      list: async (id: string) =>
        id === companyId
          ? { ok: true as const, value: [draft] }
          : { ok: false as const, error: 503 as const },
    };

    renderPage(draftClient, twoCompanyGateway);

    expect(
      await screen.findByRole("heading", { name: "Papel", level: 3 }),
    ).toBeVisible();
    expect(
      screen.getByText(/Beta.*indisponível|indisponível.*Beta/i),
    ).toBeVisible();
  });

  it("shows a human invoice attachment label without exposing its identifier", async () => {
    const invoiceFileId = "88888888-8888-4888-8888-888888888888";
    const persisted = { ...draft, invoiceFileId };
    const draftClient = {
      create: async () => ({ ok: true as const, value: persisted }),
      detail: async () => ({ ok: true as const, value: persisted }),
      list: async () => ({ ok: true as const, value: [persisted] }),
    };

    renderPage(draftClient);

    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Visualizar" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Fatura anexada")).toBeVisible();
    expect(dialog.textContent).not.toContain(invoiceFileId);
  });

  it("submits through the modal and resets it after the async create completes", async () => {
    const user = userEvent.setup();
    const create = vi.fn(
      async (input: {
        companyId: string;
        description: string;
        requestId: string;
        title: string;
      }) => ({
        ok: true as const,
        value: {
          ...draft,
          companyId: input.companyId,
          description: input.description,
          requestId: input.requestId,
          title: input.title,
        },
      }),
    );
    const draftClient = {
      create,
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    renderPage(draftClient);

    await openCreate(user);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    expect(create.mock.calls[0]?.[0]).toMatchObject({
      companyId,
      description: "Descrição do rascunho",
      title: "Novo papel",
      amount: "1250.00",
      currency: "BRL",
      proofRequirement: "required",
    });
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Nova solicitação" }),
      ).toBeVisible(),
    );
    await openCreate(user);
    expect(screen.getByRole("textbox", { name: "Título" })).toHaveValue("");
    expect(screen.getByRole("textbox", { name: "Descrição" })).toHaveValue("");
  });

  it("keeps a delayed create dialog open when Escape is pressed while submitting", async () => {
    const user = userEvent.setup();
    let finishCreate: (value: { ok: true; value: typeof draft }) => void =
      () => {
        throw new Error("Create was not requested.");
      };
    const delayedCreate = new Promise<{ ok: true; value: typeof draft }>(
      (resolve) => {
        finishCreate = resolve;
      },
    );
    const draftClient = {
      create: vi.fn(() => delayedCreate),
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    renderPage(draftClient);
    await openCreate(user);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));
    expect(
      await screen.findByRole("button", { name: "Criando…" }),
    ).toBeDisabled();

    await user.keyboard("{Escape}");
    expect(screen.getByRole("dialog")).toBeVisible();
    expect(screen.getByRole("button", { name: "Criando…" })).toBeVisible();

    finishCreate({ ok: true, value: draft });
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Nova solicitação" }),
      ).toBeVisible(),
    );
  });

  it("reuses a definitive failed attempt only for the exact payload and rotates it after an edit or a new dialog", async () => {
    const user = userEvent.setup();
    const create = vi.fn(async (_input: CreatePaguaiDraftInput) => ({
      error: 400 as const,
      ok: false as const,
    }));
    const draftClient = {
      create,
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    renderPage(draftClient);
    await openCreate(user);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    const firstPayload = create.mock.calls[0]?.[0];

    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(2));
    expect(create.mock.calls[1]?.[0]).toEqual(firstPayload);

    fireEvent.change(screen.getByRole("textbox", { name: "Título" }), {
      target: { value: "Novo papel editado" },
    });
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(3));
    expect(create.mock.calls[2]?.[0]).toMatchObject({
      title: "Novo papel editado",
    });
    expect(create.mock.calls[2]?.[0]?.requestId).not.toBe(
      firstPayload?.requestId,
    );

    await user.click(screen.getByRole("button", { name: "Cancelar" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    await openCreate(user);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(4));
    expect(create.mock.calls[3]?.[0]?.requestId).not.toBe(
      create.mock.calls[2]?.[0]?.requestId,
    );
  });

  it("rotates the request ID after a definitive 409 so an unchanged retry is a new attempt", async () => {
    const user = userEvent.setup();
    const create = vi.fn(async (_input: CreatePaguaiDraftInput) => ({
      error: 409 as const,
      ok: false as const,
    }));
    const draftClient = {
      create,
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    renderPage(draftClient);
    await openCreate(user);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(2));

    expect(create.mock.calls[1]?.[0]?.requestId).not.toBe(
      create.mock.calls[0]?.[0]?.requestId,
    );
    expect(screen.getByRole("alert")).not.toHaveTextContent(
      "Tente novamente com os mesmos dados.",
    );
  });

  it("keeps an ambiguous create authoritative until its exact request resolves the existing draft", async () => {
    const user = userEvent.setup();
    let createCalls = 0;
    const create = vi.fn(async (input: CreatePaguaiDraftInput) => {
      createCalls += 1;
      if (createCalls === 1)
        return { error: "network" as const, ok: false as const };
      return {
        ok: true as const,
        value: {
          ...draft,
          description: input.description,
          requestId: input.requestId,
          title: input.title,
        },
      };
    });
    const draftClient = {
      create,
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    renderPage(draftClient);
    await openCreate(user);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    const originalPayload = create.mock.calls[0]?.[0];
    if (!originalPayload) throw new Error("Expected original create payload");

    const recovery = screen.getByRole("status");
    expect(recovery).toHaveTextContent(
      "Não foi possível confirmar o resultado da solicitação.",
    );
    expect(recovery).toHaveTextContent(
      "Verifique e reenvie a mesma solicitação.",
    );
    const title = screen.getByRole("textbox", { name: "Título" });
    expect(title).toBeDisabled();
    expect(
      screen.getByRole("combobox", { name: "Comprovante de pagamento" }),
    ).toHaveAttribute("aria-disabled", "true");
    expect(
      screen.getByLabelText(
        "Fatura ou documento da solicitação: anexar arquivo",
      ),
    ).toBeDisabled();
    expect(screen.getByRole("button", { name: "Cancelar" })).toBeDisabled();
    expect(
      screen.getByRole("button", {
        hidden: true,
        name: "Nova solicitação",
      }),
    ).toBeDisabled();

    await user.type(title, " não pode editar");
    expect(title).toHaveValue("Novo papel");
    await user.keyboard("{Escape}");
    expect(screen.getByRole("dialog")).toBeVisible();

    await user.click(
      screen.getByRole("button", {
        name: "Verificar e reenviar solicitação",
      }),
    );
    await waitFor(() => expect(create).toHaveBeenCalledTimes(2));
    expect(create.mock.calls[1]?.[0]).toEqual(originalPayload);
    expect(
      new Set(create.mock.calls.map(([payload]) => payload.requestId)),
    ).toEqual(new Set([originalPayload.requestId]));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(
      screen.getByRole("heading", { level: 3, name: "Novo papel" }),
    ).toBeVisible();
  });

  it("keeps the recovery lock and accessible retry action after repeated network results", async () => {
    const user = userEvent.setup();
    const create = vi.fn(async (_input: CreatePaguaiDraftInput) => ({
      error: "network" as const,
      ok: false as const,
    }));
    const draftClient = {
      create,
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    renderPage(draftClient);
    await openCreate(user);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    const originalPayload = create.mock.calls[0]?.[0];
    if (!originalPayload) throw new Error("Expected original create payload");

    await user.click(
      screen.getByRole("button", {
        name: "Verificar e reenviar solicitação",
      }),
    );
    await waitFor(() => expect(create).toHaveBeenCalledTimes(2));

    expect(screen.getByRole("status")).toHaveTextContent(
      "Não foi possível confirmar o resultado da solicitação.",
    );
    expect(
      screen.getByRole("button", {
        name: "Verificar e reenviar solicitação",
      }),
    ).toBeEnabled();
    expect(screen.getByRole("textbox", { name: "Título" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Cancelar" })).toBeDisabled();
    expect(create.mock.calls[1]?.[0]).toEqual(originalPayload);
  });

  it("does not let a late create response close or populate a changed user membership scope", async () => {
    const user = userEvent.setup();
    const createResult = deferred<{ ok: true; value: typeof draft }>();
    const create = vi.fn(
      (_input: CreatePaguaiDraftInput) => createResult.promise,
    );
    let activeBootstrap = bootstrap;
    const sessionGateway: MockSessionGateway = {
      getBootstrap: async () => activeBootstrap,
      login: async () => activeBootstrap,
      logout: async () => undefined,
    };
    const draftClient = {
      create,
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    renderPageWithSessionRefresh(draftClient, sessionGateway);
    await openCreate(user);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));

    activeBootstrap = {
      ...bootstrap,
      companies: [
        {
          ...bootstrap.companies[0],
          membershipId: "99999999-9999-4999-8999-999999999999",
        },
      ],
      user: {
        displayName: "Clara",
        id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
      },
    };
    await user.click(
      screen.getByRole("button", { hidden: true, name: "Atualizar sessão" }),
    );
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    await openCreate(user);
    await user.type(
      screen.getByRole("textbox", { name: "Título" }),
      "Solicitação da nova sessão",
    );
    await act(async () => {
      createResult.resolve({ ok: true, value: draft });
      await createResult.promise;
    });

    expect(screen.getByRole("dialog")).toBeVisible();
    expect(screen.getByRole("textbox", { name: "Título" })).toHaveValue(
      "Solicitação da nova sessão",
    );
    expect(
      screen.queryByRole("heading", { level: 3, name: "Papel" }),
    ).toBeNull();
  });

  it("does not reopen a removed company draft when its detail response arrives late", async () => {
    const user = userEvent.setup();
    const betaDraft = {
      ...draft,
      companyId: secondCompanyId,
      id: "77777777-7777-4777-8777-777777777777",
      title: "Toner",
    };
    const detailResult = deferred<{ ok: true; value: typeof betaDraft }>();
    let activeBootstrap = bootstrapFor([
      ...bootstrap.companies,
      {
        capabilities: [readCapability, createCapability],
        companyId: secondCompanyId,
        companyName: "Beta",
        membershipId: "88888888-8888-4888-8888-888888888888",
      },
    ]);
    const sessionGateway: MockSessionGateway = {
      getBootstrap: async () => activeBootstrap,
      login: async () => activeBootstrap,
      logout: async () => undefined,
    };
    const detail = vi.fn(() => detailResult.promise);
    const draftClient = {
      create: async () => ({ ok: true as const, value: draft }),
      detail,
      list: async (requestedCompanyId: string) => ({
        ok: true as const,
        value:
          requestedCompanyId === secondCompanyId ? [betaDraft] : ([] as const),
      }),
    };

    renderPageWithSessionRefresh(draftClient, sessionGateway);
    const betaHeading = await screen.findByRole("heading", {
      level: 3,
      name: "Toner",
    });
    const betaCard = betaHeading.closest(".MuiCard-root");
    if (!(betaCard instanceof HTMLElement))
      throw new Error("Expected Beta draft card");
    await user.click(
      within(betaCard).getByRole("button", { name: "Visualizar" }),
    );
    await waitFor(() =>
      expect(detail).toHaveBeenCalledWith(secondCompanyId, betaDraft.id),
    );

    activeBootstrap = bootstrap;
    await user.click(
      screen.getByRole("button", { hidden: true, name: "Atualizar sessão" }),
    );
    await waitFor(() => expect(screen.queryByText("Toner")).toBeNull());
    await act(async () => {
      detailResult.resolve({ ok: true, value: betaDraft });
      await detailResult.promise;
    });

    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("blocks submission without an explicit proof requirement selection", async () => {
    const user = userEvent.setup();
    const create = vi.fn();
    const draftClient = {
      create,
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    renderPage(draftClient);

    await openCreate(user);
    await user.type(
      screen.getByRole("textbox", { name: "Título" }),
      "Novo papel",
    );
    await user.type(
      screen.getByRole("textbox", { name: "Descrição" }),
      "Descrição",
    );
    await user.type(
      screen.getByRole("textbox", { name: "Valor (R$)" }),
      "1.250,00",
    );
    const dueDate = document.querySelector<HTMLInputElement>(
      'input[name="dueDate"]',
    );
    if (!dueDate) throw new Error("due date input missing");
    fireEvent.change(dueDate, { target: { value: "2026-09-01" } });
    await user.type(
      screen.getByRole("textbox", { name: "Quem receberá" }),
      "Fornecedor Demo",
    );
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));

    expect(create).not.toHaveBeenCalled();
    expect(screen.getByText("Selecione o comprovante.")).toBeVisible();
  });

  it("keeps payment proof requirement separate from the invoice attachment", async () => {
    const user = userEvent.setup();
    const create = vi.fn();
    const draftClient = {
      create,
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    renderPage(draftClient);

    await openCreate(user);
    await user.click(
      screen.getByRole("combobox", { name: "Comprovante de pagamento" }),
    );
    await user.click(
      await screen.findByRole("option", { name: "Obrigatório" }),
    );

    expect(
      screen.getByText("Fatura ou documento da solicitação"),
    ).toBeVisible();
    expect(screen.queryByText(/O comprovante é obrigatório/)).toBeNull();
    expect(create).not.toHaveBeenCalled();
  });

  it("rejects a malformed Brazilian amount grouping", async () => {
    const user = userEvent.setup();
    const create = vi.fn();
    const draftClient = {
      create,
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    renderPage(draftClient);

    await openCreate(user);
    await user.type(
      screen.getByRole("textbox", { name: "Título" }),
      "Novo papel",
    );
    await user.type(
      screen.getByRole("textbox", { name: "Descrição" }),
      "Descrição",
    );
    await user.type(
      screen.getByRole("textbox", { name: "Valor (R$)" }),
      "1.2,34",
    );
    const dueDate = document.querySelector<HTMLInputElement>(
      'input[name="dueDate"]',
    );
    if (!dueDate) throw new Error("due date input missing");
    fireEvent.change(dueDate, { target: { value: "2026-09-01" } });
    await user.type(
      screen.getByRole("textbox", { name: "Quem receberá" }),
      "Fornecedor Demo",
    );
    await user.click(
      screen.getByRole("combobox", { name: "Comprovante de pagamento" }),
    );
    await user.click(
      await screen.findByRole("option", { name: "Obrigatório" }),
    );
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));

    expect(create).not.toHaveBeenCalled();
    expect(screen.getByText(/valor válido em reais/)).toBeVisible();
  });

  it("keeps successful drafts visible while another authorized company fails", async () => {
    let finishSecondList: (
      v: { ok: false; error: 503 } | { ok: true; value: readonly [] },
    ) => void = () => {
      throw new Error("Second company list was not requested.");
    };
    const secondList = new Promise<
      { ok: false; error: 503 } | { ok: true; value: readonly [] }
    >((resolve) => {
      finishSecondList = resolve;
    });
    const draftClient = {
      create: async () => ({ ok: true as const, value: draft }),
      detail: async () => ({ ok: true as const, value: draft }),
      list: (id: string) =>
        id === companyId
          ? Promise.resolve({ ok: true as const, value: [draft] })
          : secondList,
    };
    const twoCompanyBootstrap = bootstrapFor([
      ...bootstrap.companies,
      {
        membershipId: "77777777-7777-4777-8777-777777777777",
        companyId: secondCompanyId,
        companyName: "Beta",
        capabilities: [readCapability, createCapability],
      },
    ]);
    const twoCompanyGateway: MockSessionGateway = {
      ...gateway,
      getBootstrap: async () => twoCompanyBootstrap,
    };

    renderPage(draftClient, twoCompanyGateway);

    expect(await screen.findByText("Carregando…")).toBeVisible();
    finishSecondList({ ok: false, error: 503 });

    await waitFor(() =>
      expect(
        screen.getByText(
          "Beta indisponível: Paguai está indisponível. Tente novamente com os mesmos dados.",
        ),
      ).toBeVisible(),
    );
    expect(
      screen.getByRole("heading", { name: "Papel", level: 3 }),
    ).toBeVisible();
  });

  it("hides create and exposes only Visualizar without create capability", async () => {
    const user = userEvent.setup();
    const readOnlyBootstrap = bootstrapFor([
      {
        membershipId: "44444444-4444-4444-8444-444444444444",
        companyId,
        companyName: "Alfa",
        capabilities: [readCapability],
      },
    ]);
    const readOnlyGateway: MockSessionGateway = {
      ...gateway,
      getBootstrap: async () => readOnlyBootstrap,
    };
    const draftClient = {
      create: async () => ({ ok: true as const, value: draft }),
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [draft] }),
    };

    renderPage(draftClient, readOnlyGateway);

    expect(
      await screen.findByRole("heading", { name: "Papel", level: 3 }),
    ).toBeVisible();
    expect(
      screen.queryByRole("button", { name: "Nova solicitação" }),
    ).toBeNull();
    await user.click(screen.getByRole("button", { name: "Visualizar" }));
    expect(await screen.findByRole("dialog")).toBeVisible();
  });
});
