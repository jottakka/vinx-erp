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
const proofFileId = "44444444-4444-4444-8444-444444444444";
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
const draftWithServerProof = {
  ...draft,
  proofFileId,
  proofFileName: "comprovante.pdf",
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
  proofUploader?: (
    file: File,
    signal: AbortSignal,
  ) => Promise<
    | {
        ok: true;
        fileId: string;
        fileName: string;
        mimeType: string;
        sizeBytes: number;
      }
    | { ok: false; error: { kind: string; message: string } }
  >,
) {
  render(
    <VinxThemeProvider>
      <SessionProvider gateway={sessionGateway}>
        <PaguaiPage
          draftClient={
            draftClient as Parameters<typeof PaguaiPage>[0]["draftClient"]
          }
          proofUploader={
            proofUploader as Parameters<typeof PaguaiPage>[0]["proofUploader"]
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
      within(dialog).getByText("Estado: rascunho · versão 1"),
    ).toBeVisible();
    expect(
      within(dialog).getAllByText("R$ 1.250,00").length,
    ).toBeGreaterThanOrEqual(1);
    expect(within(dialog).getByText("Vencimento: 01/09/2026")).toBeVisible();
    expect(
      within(dialog).getByText(/Beneficiário: Fornecedor Demo/),
    ).toBeVisible();
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

  it("warns when proof is required but no file is attached", async () => {
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
      screen.getByText(
        "O comprovante é obrigatório neste rascunho. Anexe um arquivo antes de criar.",
      ),
    ).toBeVisible();
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

  it("clears company A drafts before company B fails to load", async () => {
    const user = userEvent.setup();
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

    expect(
      await screen.findByRole("heading", { name: "Papel", level: 3 }),
    ).toBeVisible();
    await user.click(screen.getByRole("combobox", { name: "Empresa" }));
    await user.click(await screen.findByRole("option", { name: "Beta" }));

    expect(screen.getByText("Carregando…")).toBeVisible();
    expect(
      screen.queryByRole("heading", { name: "Papel", level: 3 }),
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
      screen.queryByRole("heading", { name: "Papel", level: 3 }),
    ).toBeNull();
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

  it("persists the dialog proof file id into the create payload", async () => {
    const user = userEvent.setup();
    const proofUploader = vi.fn(async (file: File) => ({
      ok: true as const,
      fileId: proofFileId,
      fileName: file.name,
      mimeType: "application/pdf",
      sizeBytes: file.size,
    }));
    const create = vi.fn(async (input: { proofFileId?: string | null }) => ({
      ok: true as const,
      value: { ...draft, proofFileId: input.proofFileId ?? null },
    }));
    const draftClient = {
      create,
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    renderPage(draftClient, gateway, proofUploader);

    await openCreate(user);
    await fillForm(user);
    const attachInput = document.querySelector<HTMLInputElement>(
      'input[aria-label="Anexar comprovante: anexar arquivo"]',
    );
    if (!attachInput) throw new Error("attachment input missing");
    await user.upload(attachInput, new File(["pdf"], "comprovante.pdf"));
    expect(await screen.findByText("comprovante.pdf")).toBeVisible();

    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    expect(create.mock.calls[0]?.[0]).toMatchObject({
      proofFileId,
    });
  });

  it("surfaces the server-owned attachment after a fresh reload read", async () => {
    const user = userEvent.setup();
    const draftClient = {
      create: async () => ({ ok: true as const, value: draftWithServerProof }),
      detail: async () => ({ ok: true as const, value: draftWithServerProof }),
      list: async () => ({ ok: true as const, value: [draftWithServerProof] }),
    };

    renderPage(draftClient);

    await user.click(await screen.findByRole("button", { name: "Visualizar" }));

    const dialog = await screen.findByRole("dialog");
    // A anexação vem da leitura autorizada do servidor, não do estado local.
    expect(
      within(dialog).getByText(/Comprovante anexado \(comprovante\.pdf\)/),
    ).toBeVisible();
  });

  it("renders the typed conflict code when the proof is already bound", async () => {
    const user = userEvent.setup();
    const draftClient = {
      create: async () =>
        ({
          ok: false as const,
          error: { status: 409, code: "proof_file_bound" },
        }) as const,
      detail: async () => ({ ok: true as const, value: draft }),
      list: async () => ({ ok: true as const, value: [] }),
    };

    renderPage(draftClient);

    await openCreate(user);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));

    expect(
      await screen.findByText(
        "Este comprovante já está vinculado a outra solicitação.",
      ),
    ).toBeVisible();
  });
});
