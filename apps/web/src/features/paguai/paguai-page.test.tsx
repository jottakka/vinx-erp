import { fireEvent, render, screen, waitFor } from "@testing-library/react";
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
  proofRequirement: "required" as const,
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
  it("renders the selected draft detail with the financial fields", async () => {
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
    expect(screen.getAllByText("R$ 1.250,00").length).toBeGreaterThanOrEqual(1);
    expect(
      screen.getAllByText("Vencimento: 01/09/2026").length,
    ).toBeGreaterThanOrEqual(1);
    expect(screen.getByText(/Beneficiário: Fornecedor Demo/)).toBeVisible();
  });

  it("clears company A drafts before company B fails to load", async () => {
    const user = userEvent.setup();
    let finishSecondList: (
      result: { ok: false; error: 503 } | { ok: true; value: readonly [] },
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
    const twoCompanyBootstrap: BootstrapSnapshot = {
      ...bootstrap,
      companies: [
        ...bootstrap.companies,
        {
          membershipId: "77777777-7777-4777-8777-777777777777",
          companyId: secondCompanyId,
          companyName: "Beta",
          capabilities: [
            { name: "paguai.draft.read", scopes: ["Company"] },
            { name: "paguai.draft.create", scopes: ["Company"] },
          ],
        },
      ],
    };
    const twoCompanyGateway: MockSessionGateway = {
      ...gateway,
      getBootstrap: async () => twoCompanyBootstrap,
    };

    render(
      <VinxThemeProvider>
        <SessionProvider gateway={twoCompanyGateway}>
          <PaguaiPage draftClient={draftClient} />
        </SessionProvider>
      </VinxThemeProvider>,
    );

    expect(
      await screen.findByRole("button", { name: /Papel.*rascunho/ }),
    ).toBeVisible();
    await user.click(screen.getByRole("combobox", { name: "Empresa" }));
    await user.click(await screen.findByRole("option", { name: "Beta" }));

    expect(screen.getByText("Carregando…")).toBeVisible();
    expect(
      screen.queryByRole("button", { name: /Papel.*rascunho/ }),
    ).toBeNull();
    finishSecondList({ ok: false, error: 503 });

    await waitFor(() =>
      expect(
        screen.getByText(
          "Paguai está indisponível. Tente novamente com o mesmo identificador.",
        ),
      ).toBeVisible(),
    );
    expect(
      screen.queryByRole("button", { name: /Papel.*rascunho/ }),
    ).toBeNull();
  });

  it("submits through the form and resets it after the async create completes", async () => {
    const user = userEvent.setup();
    const create = vi.fn(async (input) => ({
      ok: true as const,
      value: {
        ...draft,
        companyId: input.companyId,
        description: input.description,
        requestId: input.requestId,
        title: input.title,
      },
    }));
    const draftClient = {
      create,
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    render(
      <VinxThemeProvider>
        <SessionProvider gateway={gateway}>
          <PaguaiPage draftClient={draftClient} />
        </SessionProvider>
      </VinxThemeProvider>,
    );

    const title = await screen.findByRole("textbox", { name: "Título" });
    const description = screen.getByRole("textbox", { name: "Descrição" });
    const amount = screen.getByRole("textbox", { name: "Valor (R$)" });
    const dueDate = document.querySelector<HTMLInputElement>(
      'input[name="dueDate"]',
    );
    const beneficiary = screen.getByRole("textbox", {
      name: "Beneficiário (informativo)",
    });
    await user.type(title, "Novo papel");
    await user.type(description, "Descrição do rascunho");
    await user.type(amount, "1.250,00");
    if (!dueDate) throw new Error("due date input missing");
    fireEvent.change(dueDate, { target: { value: "2026-09-01" } });
    await user.type(beneficiary, "Fornecedor Demo");
    await user.click(screen.getByRole("combobox", { name: "Comprovante" }));
    await user.click(
      await screen.findByRole("option", { name: "Obrigatório" }),
    );
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    expect(title).toHaveValue("");
    expect(description).toHaveValue("");
    expect(create.mock.calls[0]?.[0]).toMatchObject({
      companyId,
      description: "Descrição do rascunho",
      title: "Novo papel",
      amount: "1250.00",
      currency: "BRL",
      dueDate: expect.any(String),
      beneficiaryDisplayName: "Fornecedor Demo",
      proofRequirement: "required",
    });
    expect(
      screen.getByRole("heading", { name: "Novo papel", level: 2 }),
    ).toBeVisible();
  });

  it("blocks draft creation without an explicit proof requirement selection", async () => {
    const user = userEvent.setup();
    const create = vi.fn();
    const draftClient = {
      create,
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    render(
      <VinxThemeProvider>
        <SessionProvider gateway={gateway}>
          <PaguaiPage draftClient={draftClient} />
        </SessionProvider>
      </VinxThemeProvider>,
    );

    const title = await screen.findByRole("textbox", { name: "Título" });
    const description = screen.getByRole("textbox", { name: "Descrição" });
    const amount = screen.getByRole("textbox", { name: "Valor (R$)" });
    const dueDate = document.querySelector<HTMLInputElement>(
      'input[name="dueDate"]',
    );
    const beneficiary = screen.getByRole("textbox", {
      name: "Beneficiário (informativo)",
    });
    await user.type(title, "Novo papel");
    await user.type(description, "Descrição do rascunho");
    await user.type(amount, "1.250,00");
    if (!dueDate) throw new Error("due date input missing");
    fireEvent.change(dueDate, { target: { value: "2026-09-01" } });
    await user.type(beneficiary, "Fornecedor Demo");
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));

    expect(create).not.toHaveBeenCalled();
    expect(
      screen.queryByRole("heading", { name: "Novo papel", level: 2 }),
    ).toBeNull();
  });

  it("rejects malformed Brazilian amount grouping before creating", async () => {
    const user = userEvent.setup();
    const create = vi.fn();
    const draftClient = {
      create,
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    render(
      <VinxThemeProvider>
        <SessionProvider gateway={gateway}>
          <PaguaiPage draftClient={draftClient} />
        </SessionProvider>
      </VinxThemeProvider>,
    );

    await user.type(
      await screen.findByRole("textbox", { name: "Título" }),
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
    expect(screen.getByText("Revise os campos obrigatórios.")).toBeVisible();
  });
});
