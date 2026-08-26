import {
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
import { SessionProvider } from "../auth/session-provider";
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

async function openCreate(user: ReturnType<typeof userEvent.setup>) {
  await user.click(
    await screen.findByRole("button", { name: "Nova solicitação" }),
  );
  await screen.findByRole("heading", { name: "Novo rascunho" });
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
    screen.getByRole("textbox", { name: "Beneficiário (informativo)" }),
    "Fornecedor Demo",
  );
  await user.click(screen.getByRole("combobox", { name: "Comprovante" }));
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
    expect(screen.queryByRole("dialog")).toBeNull();

    await openCreate(user);
    expect(await screen.findByRole("dialog")).toBeVisible();
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
      within(dialog).getByText("Estado: Rascunho · versão 1"),
    ).toBeVisible();
    expect(
      within(dialog).getAllByText("R$ 1.250,00").length,
    ).toBeGreaterThanOrEqual(1);
    expect(within(dialog).getByText("Vencimento: 01/09/2026")).toBeVisible();
    expect(
      within(dialog).getByText(/Beneficiário: Fornecedor Demo/),
    ).toBeVisible();
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
    expect(screen.getByLabelText("Empresa Alfa")).toBeVisible();
    expect(screen.getByLabelText("Empresa Beta")).toBeVisible();
    expect(screen.getAllByText("Rascunho")).toHaveLength(2);
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

  it("shows the persisted invoice identifier after opening detail", async () => {
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
    expect(within(dialog).getByText(new RegExp(invoiceFileId))).toBeVisible();
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
      screen.getByRole("textbox", { name: "Beneficiário (informativo)" }),
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
    await user.click(screen.getByRole("combobox", { name: "Comprovante" }));
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
      screen.getByRole("textbox", { name: "Beneficiário (informativo)" }),
      "Fornecedor Demo",
    );
    await user.click(screen.getByRole("combobox", { name: "Comprovante" }));
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
          "Beta indisponível: Paguai está indisponível. Tente novamente com o mesmo identificador.",
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
