import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
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
import type { MockSessionGateway } from "../auth/session-contract";
import { SessionProvider } from "../auth/session-provider";
import type {
  EmployeeDirectoryCreateCommand,
  EmployeeDirectoryCreateResult,
  EmployeeDirectoryGateway,
} from "./employee-directory-client";
import { PeoplePage } from "./people-page";

const companyId = "11111111-1111-4111-8111-111111111111";
const secondCompanyId = "22222222-2222-4222-8222-222222222222";

function createGateway(
  overrides: Partial<EmployeeDirectoryGateway> = {},
): EmployeeDirectoryGateway {
  return {
    create: vi.fn().mockResolvedValue({ ok: true, state: "created" }),
    list: vi.fn().mockImplementation(async (requestedCompanyId: string) => ({
      items: [
        requestedCompanyId === secondCompanyId
          ? {
              displayName: "Bia",
              employeeId: "77777777-7777-4777-8777-777777777777",
              membershipId: "88888888-8888-4888-8888-888888888888",
            }
          : {
              displayName: "Ana",
              employeeId: "33333333-3333-4333-8333-333333333333",
              membershipId: "44444444-4444-4444-8444-444444444444",
            },
      ],
      ok: true,
    })),
    ...overrides,
  };
}

function snapshot(
  capability = true,
  userId = "user-1",
  includeSecondCompany = false,
) {
  return {
    companies: capability
      ? [
          {
            capabilities: [
              { name: "foundation.employees.create", scopes: ["Company"] },
            ],
            companyId,
            companyName: "Alfa",
            membershipId: "55555555-5555-4555-8555-555555555555",
          },
          {
            capabilities: [
              { name: "foundation.employees.create", scopes: ["Company"] },
            ],
            companyId: secondCompanyId,
            companyName: "Beta",
            membershipId: "66666666-6666-4666-8666-666666666666",
          },
        ].filter((_company, index) => includeSecondCompany || index === 0)
      : [],
    flags: {},
    platform: { isPlatformOperator: false },
    user: { displayName: "Maria", id: userId },
  } as const;
}

function snapshotForCompanies(
  companyIds: readonly string[],
  userId = "user-1",
) {
  const current = snapshot(true, userId, true);
  return {
    ...current,
    companies: current.companies.filter((company) =>
      companyIds.includes(company.companyId),
    ),
  };
}

function deferred<T>() {
  let resolve: (value: T) => void = () => undefined;
  const promise = new Promise<T>((nextResolve) => {
    resolve = nextResolve;
  });
  return { promise, resolve };
}

function createSessionGateway(
  getBootstrap = vi.fn().mockResolvedValue(snapshot()),
): MockSessionGateway {
  return {
    getBootstrap,
    login: vi.fn(),
    logout: vi.fn(),
  };
}

function renderPage({
  gateway = createGateway(),
  sessionGateway = createSessionGateway(),
  createRequestId,
}: {
  gateway?: EmployeeDirectoryGateway;
  sessionGateway?: MockSessionGateway;
  createRequestId?: () => string;
} = {}) {
  const queryClient = new QueryClient({
    defaultOptions: { mutations: { retry: false }, queries: { retry: false } },
  });

  render(
    <VinxThemeProvider>
      <QueryClientProvider client={queryClient}>
        <SessionProvider gateway={sessionGateway}>
          <PeoplePage
            gateway={gateway}
            {...(createRequestId ? { createRequestId } : {})}
          />
        </SessionProvider>
      </QueryClientProvider>
    </VinxThemeProvider>,
  );

  return { gateway, queryClient, sessionGateway };
}

async function selectCompany(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole("combobox", { name: "Empresas" }));
  await user.click(await screen.findByRole("option", { name: /Alfa/ }));
  await user.keyboard("{Escape}");
}

async function selectSecondCompany(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole("combobox", { name: "Empresas" }));
  await user.click(
    await screen.findByRole("option", {
      name: /Todas as empresas autorizadas/,
    }),
  );
  await user.keyboard("{Escape}");
  await user.click(await screen.findByRole("combobox", { name: "Empresas" }));
  await user.click(await screen.findByRole("option", { name: /Beta/ }));
  await user.keyboard("{Escape}");
}

async function fillValidForm(user: ReturnType<typeof userEvent.setup>) {
  await user.click(
    await screen.findByRole("button", { name: "Cadastrar funcionário" }),
  );
  const dialog = await screen.findByRole("dialog");
  await user.click(within(dialog).getByRole("combobox", { name: "Empresa" }));
  await user.click(await screen.findByRole("option", { name: /Alfa/ }));
  await user.type(within(dialog).getByLabelText("Nome"), "Nova Pessoa");
  await user.type(within(dialog).getByLabelText("E-mail"), "nova@example.test");
}

async function waitForAuthenticatedDirectory(hidden = false) {
  await waitFor(() =>
    expect(
      screen.queryByRole("progressbar", { name: "Carregando pessoas" }),
    ).toBeNull(),
  );
  return screen.findByRole("button", {
    hidden,
    name: "Cadastrar funcionário",
  });
}

function muiSelectValue(combobox: HTMLElement): string {
  const input = combobox.parentElement?.querySelector<HTMLInputElement>(
    'input[aria-hidden="true"]',
  );
  if (!input) throw new Error("MUI select hidden input was not found");
  return input.value;
}

async function changeCreationCompany(
  user: ReturnType<typeof userEvent.setup>,
  name: RegExp,
) {
  const dialog = screen.getByRole("dialog");
  await user.click(within(dialog).getByRole("combobox", { name: "Empresa" }));
  await user.click(await screen.findByRole("option", { name }));
}

describe("PeoplePage", () => {
  it("does not call the gateway when no company is eligible", async () => {
    const gateway = createGateway();
    renderPage({
      gateway,
      sessionGateway: createSessionGateway(
        vi.fn().mockResolvedValue(snapshot(false)),
      ),
    });

    expect(
      await screen.findByText(
        "Nenhuma empresa está disponível para esta ação.",
      ),
    ).toBeVisible();
    expect(gateway.list).not.toHaveBeenCalled();
    expect(gateway.create).not.toHaveBeenCalled();
  });

  it("uses plain directory wording when no employees are found", async () => {
    const gateway = createGateway({
      list: vi.fn().mockResolvedValue({ items: [], ok: true }),
    });
    renderPage({ gateway });

    expect(
      await screen.findByText("Nenhum funcionário encontrado."),
    ).toBeVisible();
    expect(
      screen.queryByText("Nenhum funcionário reservado."),
    ).not.toBeInTheDocument();
  });

  it("shows a Portuguese name limit instead of the default Zod message", async () => {
    const user = userEvent.setup();
    renderPage();
    await user.click(
      await screen.findByRole("button", { name: "Cadastrar funcionário" }),
    );
    const dialog = await screen.findByRole("dialog");
    await changeCreationCompany(user, /Alfa/);
    const name = within(dialog).getByLabelText("Nome");
    fireEvent.change(name, { target: { value: "x".repeat(201) } });
    const form = name.closest("form");
    if (!form) throw new Error("Expected reservation form");
    fireEvent.submit(form);

    expect(
      await within(dialog).findByText(
        "O nome deve ter no máximo 200 caracteres.",
      ),
    ).toBeVisible();
    expect(
      within(dialog).queryByText(/String must contain/),
    ).not.toBeInTheDocument();
  });

  it("defaults to all eligible companies and only renders human directory data", async () => {
    const gateway = createGateway({
      list: vi.fn().mockResolvedValue({
        items: [
          {
            displayName: "<img src=x onerror=alert(1)>",
            employeeId: "33333333-3333-4333-8333-333333333333",
            membershipId: "44444444-4444-4444-8444-444444444444",
          },
        ],
        ok: true,
      }),
    });
    renderPage({
      gateway,
      sessionGateway: createSessionGateway(
        vi.fn().mockResolvedValue(snapshot(true, "user-1", true)),
      ),
    });

    expect(
      await screen.findAllByText("<img src=x onerror=alert(1)>"),
    ).toHaveLength(2);
    await waitFor(() => expect(gateway.list).toHaveBeenCalledTimes(2));
    expect(gateway.list).toHaveBeenCalledWith(companyId, expect.anything());
    expect(gateway.list).toHaveBeenCalledWith(
      secondCompanyId,
      expect.anything(),
    );
    expect(document.querySelector("img")).toBeNull();
    expect(
      screen.queryByText("33333333-3333-4333-8333-333333333333"),
    ).toBeNull();
    expect(
      screen.queryByText("44444444-4444-4444-8444-444444444444"),
    ).toBeNull();
    expect(
      screen.getAllByRole("group", { name: "Empresa Alfa" }),
    ).not.toHaveLength(0);
    expect(
      screen.getAllByRole("group", { name: "Empresa Beta" }),
    ).not.toHaveLength(0);
  });

  it("validates locally before reserving", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    renderPage({
      gateway,
      sessionGateway: createSessionGateway(
        vi.fn().mockResolvedValue(snapshot(true, "user-1", true)),
      ),
    });

    await selectCompany(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );
    await changeCreationCompany(user, /Alfa/);
    await user.click(
      within(screen.getByRole("dialog")).getByRole("button", {
        name: "Cadastrar funcionário",
      }),
    );
    expect(
      within(screen.getByRole("dialog")).getByText(
        "Este cadastro não envia convite nem concede acesso ao sistema.",
      ),
    ).toBeVisible();

    expect(await screen.findByText("Informe um nome válido.")).toBeVisible();
    expect(gateway.create).not.toHaveBeenCalled();
  });

  it("refreshes bootstrap before reloading the selected directory after success", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    const getBootstrap = vi
      .fn()
      .mockResolvedValueOnce(snapshot())
      .mockResolvedValueOnce(snapshot());
    renderPage({
      createRequestId: () => "77777777-7777-4777-8777-777777777777",
      gateway,
      sessionGateway: createSessionGateway(getBootstrap),
    });

    await selectCompany(user);
    await screen.findByText("Ana");
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );

    await waitFor(() => expect(gateway.create).toHaveBeenCalledTimes(1));
    expect(gateway.create).toHaveBeenCalledWith({
      companyId,
      displayName: "Nova Pessoa",
      email: "nova@example.test",
      requestId: "77777777-7777-4777-8777-777777777777",
    });
    await waitFor(() => expect(getBootstrap).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(gateway.list).toHaveBeenCalledTimes(2));
    expect(await screen.findByText("Funcionário cadastrado.")).toBeVisible();
  });

  it("reconciles a delayed eligible refresh before publishing success and refetch", async () => {
    const user = userEvent.setup();
    const refresh = deferred<ReturnType<typeof snapshot>>();
    const getBootstrap = vi
      .fn()
      .mockResolvedValueOnce(snapshot())
      .mockReturnValueOnce(refresh.promise);
    const gateway = createGateway();
    renderPage({ gateway, sessionGateway: createSessionGateway(getBootstrap) });

    await selectCompany(user);
    await screen.findByText("Ana");
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );
    expect(screen.queryByText("Funcionário cadastrado.")).toBeNull();

    await waitFor(() => expect(getBootstrap).toHaveBeenCalledTimes(2));
    await act(async () => refresh.resolve(snapshot()));

    expect(await screen.findByText("Funcionário cadastrado.")).toBeVisible();
    await waitFor(() => expect(gateway.list).toHaveBeenCalledTimes(2));
  });

  it("does not publish or refetch a delayed refresh that removes capability", async () => {
    const user = userEvent.setup();
    const refresh = deferred<ReturnType<typeof snapshot>>();
    const getBootstrap = vi
      .fn()
      .mockResolvedValueOnce(snapshot())
      .mockReturnValueOnce(refresh.promise);
    const gateway = createGateway();
    renderPage({ gateway, sessionGateway: createSessionGateway(getBootstrap) });

    await selectCompany(user);
    await screen.findByText("Ana");
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );
    refresh.resolve(snapshot(false));

    expect(
      await screen.findByText(
        "Nenhuma empresa está disponível para esta ação.",
      ),
    ).toBeVisible();
    expect(gateway.list).toHaveBeenCalledTimes(1);
    expect(screen.queryByText("Funcionário cadastrado.")).toBeNull();
    expect(
      screen.queryByRole("button", { name: "Tentar novamente" }),
    ).toBeNull();
  });

  it("does not reuse a selected company after a delayed user change", async () => {
    const user = userEvent.setup();
    const refresh = deferred<ReturnType<typeof snapshot>>();
    const getBootstrap = vi
      .fn()
      .mockResolvedValueOnce(snapshot())
      .mockReturnValueOnce(refresh.promise);
    const gateway = createGateway();
    renderPage({ gateway, sessionGateway: createSessionGateway(getBootstrap) });

    await selectCompany(user);
    await screen.findByText("Ana");
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );
    await waitFor(() => expect(getBootstrap).toHaveBeenCalledTimes(2));
    await act(async () => refresh.resolve(snapshot(true, "user-2")));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    const createButton = await waitForAuthenticatedDirectory(true);
    await screen.findByRole("combobox", { hidden: true, name: "Empresas" });
    await waitFor(() => expect(gateway.list).toHaveBeenCalledTimes(2));
    expect(screen.queryByText("Funcionário cadastrado.")).toBeNull();
    await user.click(createButton);
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByLabelText("Nome")).toHaveValue("");
    expect(within(dialog).getByLabelText("E-mail")).toHaveValue("");
    expect(
      muiSelectValue(within(dialog).getByRole("combobox", { name: "Empresa" })),
    ).toBe("");
  });

  it("clears the selected company and form when its capability is removed", async () => {
    const user = userEvent.setup();
    const refresh = deferred<ReturnType<typeof snapshot>>();
    const getBootstrap = vi
      .fn()
      .mockResolvedValueOnce(snapshot())
      .mockReturnValueOnce(refresh.promise);
    renderPage({ sessionGateway: createSessionGateway(getBootstrap) });

    await selectCompany(user);
    await fillValidForm(user);
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByLabelText("Nome")).toHaveValue("Nova Pessoa");
    expect(within(dialog).getByLabelText("E-mail")).toHaveValue(
      "nova@example.test",
    );
    expect(
      within(dialog).getByRole("combobox", { name: "Empresa" }),
    ).toHaveTextContent("Alfa");
    expect(
      within(dialog).getByRole("button", { name: "Cadastrar funcionário" }),
    ).toBeEnabled();
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );
    refresh.resolve(snapshotForCompanies([secondCompanyId]));

    await screen.findByLabelText("Empresa");
    expect(screen.getByLabelText("Nome")).toHaveValue("");
    expect(screen.getByLabelText("E-mail")).toHaveValue("");
    expect(
      screen.getByRole("combobox", { name: "Empresa" }),
    ).not.toHaveTextContent("Alfa");
    await user.click(screen.getByRole("combobox", { name: "Empresa" }));
    expect(await screen.findByRole("option", { name: /Beta/ })).toBeVisible();
  });

  it("clears typed unselected fields before rendering a different user view", async () => {
    const user = userEvent.setup();
    const refresh = deferred<ReturnType<typeof snapshot>>();
    const getBootstrap = vi
      .fn()
      .mockResolvedValueOnce(snapshot())
      .mockReturnValueOnce(refresh.promise);
    renderPage({ sessionGateway: createSessionGateway(getBootstrap) });

    await fillValidForm(user);
    act(() => window.dispatchEvent(new Event("online")));
    await waitFor(() => expect(getBootstrap).toHaveBeenCalledTimes(2));
    await act(async () => refresh.resolve(snapshot(true, "user-2")));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    await user.click(await waitForAuthenticatedDirectory(true));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByLabelText("Nome")).toHaveValue("");
    expect(within(dialog).getByLabelText("E-mail")).toHaveValue("");
    expect(
      muiSelectValue(within(dialog).getByRole("combobox", { name: "Empresa" })),
    ).toBe("");
  });

  it("uses a bound default request ID factory", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    renderPage({ gateway, createRequestId: undefined });

    await selectCompany(user);
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );

    await waitFor(() => expect(gateway.create).toHaveBeenCalledTimes(1));
    expect(gateway.create).toHaveBeenCalledWith(
      expect.objectContaining({
        requestId: expect.stringMatching(
          /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
        ),
      }),
    );
  });

  it("offers one explicit retry with the same request after an ambiguous result", async () => {
    const user = userEvent.setup();
    const command = vi.fn<(command: EmployeeDirectoryCreateCommand) => void>();
    const gateway = createGateway({
      create: vi
        .fn()
        .mockImplementationOnce(async (next) => {
          command(next);
          return { error: { kind: "network" }, ok: false };
        })
        .mockImplementationOnce(async (next) => {
          command(next);
          return { ok: true, state: "replay" };
        }),
    });
    renderPage({ gateway });

    await selectCompany(user);
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );
    expect(
      await screen.findByRole("button", { name: "Tentar novamente" }),
    ).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Tentar novamente" }));

    await waitFor(() => expect(gateway.create).toHaveBeenCalledTimes(2));
    expect(command.mock.calls[0][0]).toEqual(command.mock.calls[1][0]);
  });

  it("blocks normal submit after ambiguity and clears both fields on company change", async () => {
    const user = userEvent.setup();
    const createRequestId = vi
      .fn()
      .mockReturnValueOnce("77777777-7777-4777-8777-777777777777")
      .mockReturnValueOnce("88888888-8888-4888-8888-888888888888");
    const gateway = createGateway({
      create: vi.fn().mockResolvedValue({
        error: { kind: "network" },
        ok: false,
      }),
    });
    renderPage({
      createRequestId,
      gateway,
      sessionGateway: createSessionGateway(
        vi.fn().mockResolvedValue(snapshot(true, "user-1", true)),
      ),
    });

    await selectCompany(user);
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );
    await screen.findByRole("button", { name: "Tentar novamente" });

    expect(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    ).toBeDisabled();
    const form = screen.getByLabelText("Nome").closest("form");
    if (!form) throw new Error("Expected reservation form");
    fireEvent.submit(form);
    expect(gateway.create).toHaveBeenCalledTimes(1);
    expect(createRequestId).toHaveBeenCalledTimes(1);

    await changeCreationCompany(user, /Beta/);
    expect(screen.getByLabelText("Nome")).toHaveValue("");
    expect(screen.getByLabelText("E-mail")).toHaveValue("");
    expect(
      screen.queryByRole("button", { name: "Tentar novamente" }),
    ).toBeNull();
  });

  it.each([
    { kind: "protocol", mayHaveCommitted: true, status: 201 },
    { kind: "unavailable", mayHaveCommitted: true, status: 500 },
  ] as const)(
    "preserves the exact command for explicit retry after a potentially committed $status result",
    async (error) => {
      const user = userEvent.setup();
      const commands =
        vi.fn<(command: EmployeeDirectoryCreateCommand) => void>();
      const gateway = createGateway({
        create: vi
          .fn()
          .mockImplementationOnce(async (command) => {
            commands(command);
            return { error, ok: false };
          })
          .mockImplementationOnce(async (command) => {
            commands(command);
            return { ok: true, state: "replay" };
          }),
      });
      renderPage({ gateway });

      await selectCompany(user);
      await fillValidForm(user);
      await user.click(
        screen.getByRole("button", { name: "Cadastrar funcionário" }),
      );
      await user.click(
        await screen.findByRole("button", { name: "Tentar novamente" }),
      );

      await waitFor(() => expect(gateway.create).toHaveBeenCalledTimes(2));
      expect(commands.mock.calls[0][0]).toEqual(commands.mock.calls[1][0]);
    },
  );

  it("disables duplicate reservation while the mutation is pending", async () => {
    const user = userEvent.setup();
    let resolveCreate:
      | ((value: EmployeeDirectoryCreateResult) => void)
      | undefined;
    const gateway = createGateway({
      create: vi.fn(
        () =>
          new Promise<EmployeeDirectoryCreateResult>((resolve) => {
            resolveCreate = resolve;
          }),
      ),
    });
    renderPage({
      gateway,
      sessionGateway: createSessionGateway(
        vi.fn().mockResolvedValue(snapshot(true, "user-1", true)),
      ),
    });

    await selectCompany(user);
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );

    expect(gateway.create).toHaveBeenCalledTimes(1);
    expect(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    ).toBeDisabled();
    expect(resolveCreate).toBeDefined();
    resolveCreate?.({ ok: true, state: "created" });
  });

  it("synchronously ignores a second submit before React publishes pending state", async () => {
    const gateway = createGateway({
      create: vi.fn().mockReturnValue(new Promise(() => undefined)),
    });
    renderPage({ gateway });
    const user = userEvent.setup();

    await selectCompany(user);
    await fillValidForm(user);
    const form = screen.getByLabelText("Nome").closest("form");
    if (!form) throw new Error("Expected reservation form");

    fireEvent.submit(form);
    fireEvent.submit(form);

    await waitFor(() => expect(gateway.create).toHaveBeenCalledTimes(1));
  });

  it("blocks a new submit while creation reconciliation is pending", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    const getBootstrap = vi.fn().mockResolvedValue(snapshot());
    renderPage({
      gateway,
      sessionGateway: createSessionGateway(getBootstrap),
    });

    await selectCompany(user);
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );
    await waitFor(() => expect(gateway.create).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(getBootstrap).toHaveBeenCalledTimes(2));

    const form = screen.getByLabelText("Nome").closest("form");
    if (!form) throw new Error("Expected reservation form");
    fireEvent.submit(form);
    fireEvent.submit(form);

    expect(gateway.create).toHaveBeenCalledTimes(1);
    await waitFor(() =>
      expect(screen.getByText("Funcionário cadastrado.")).toBeVisible(),
    );
    expect(gateway.create).toHaveBeenCalledTimes(1);
  });

  it("does not publish an old ambiguous attempt after company or form changes", async () => {
    const user = userEvent.setup();
    let resolveCreate:
      | ((value: EmployeeDirectoryCreateResult) => void)
      | undefined;
    const gateway = createGateway({
      create: vi.fn(
        () =>
          new Promise<EmployeeDirectoryCreateResult>((resolve) => {
            resolveCreate = resolve;
          }),
      ),
    });
    renderPage({
      gateway,
      sessionGateway: createSessionGateway(
        vi.fn().mockResolvedValue(snapshot(true, "user-1", true)),
      ),
    });

    await selectCompany(user);
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );
    await changeCreationCompany(user, /Beta/);
    await user.clear(screen.getByLabelText("Nome"));
    await user.type(screen.getByLabelText("Nome"), "Pessoa nova");
    resolveCreate?.({ error: { kind: "network" }, ok: false });

    await waitFor(() =>
      expect(gateway.list).toHaveBeenCalledWith(
        secondCompanyId,
        expect.anything(),
      ),
    );
    expect(
      screen.queryByRole("button", { name: "Tentar novamente" }),
    ).toBeNull();
    expect(screen.getByLabelText("Nome")).toHaveValue("Pessoa nova");
  });

  it("does not reset newer fields or refetch an old company after a late success", async () => {
    const user = userEvent.setup();
    let resolveCreate:
      | ((value: EmployeeDirectoryCreateResult) => void)
      | undefined;
    const gateway = createGateway({
      create: vi.fn(
        () =>
          new Promise<EmployeeDirectoryCreateResult>((resolve) => {
            resolveCreate = resolve;
          }),
      ),
    });
    renderPage({
      gateway,
      sessionGateway: createSessionGateway(
        vi.fn().mockResolvedValue(snapshot(true, "user-1", true)),
      ),
    });

    await selectCompany(user);
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );
    await changeCreationCompany(user, /Beta/);
    await user.clear(screen.getByLabelText("Nome"));
    await user.type(screen.getByLabelText("Nome"), "Pessoa atual");
    resolveCreate?.({ ok: true, state: "created" });

    await waitFor(() => expect(gateway.list).toHaveBeenCalledTimes(2));
    expect(screen.getByLabelText("Nome")).toHaveValue("Pessoa atual");
    expect(screen.queryByText("Funcionário cadastrado.")).toBeNull();
  });

  it("does not render a delayed directory response from a previous company", async () => {
    const user = userEvent.setup();
    type DirectoryListResult = Awaited<
      ReturnType<EmployeeDirectoryGateway["list"]>
    >;
    let resolveFirstList: ((value: DirectoryListResult) => void) | undefined;
    const gateway = createGateway({
      list: vi.fn((requestedCompanyId: string) => {
        if (requestedCompanyId === companyId) {
          return new Promise<DirectoryListResult>((resolve) => {
            resolveFirstList = resolve;
          });
        }
        return Promise.resolve<DirectoryListResult>({
          items: [
            {
              displayName: "Bia",
              employeeId: "77777777-7777-4777-8777-777777777777",
              membershipId: "88888888-8888-4888-8888-888888888888",
            },
          ],
          ok: true,
        });
      }),
    });
    renderPage({
      gateway,
      sessionGateway: createSessionGateway(
        vi.fn().mockResolvedValue(snapshot(true, "user-1", true)),
      ),
    });

    await selectCompany(user);
    await waitFor(() =>
      expect(gateway.list).toHaveBeenCalledWith(companyId, expect.anything()),
    );

    await selectSecondCompany(user);
    expect(await screen.findByText("Bia")).toBeVisible();

    await act(async () => {
      resolveFirstList?.({
        items: [
          {
            displayName: "Ana",
            employeeId: "33333333-3333-4333-8333-333333333333",
            membershipId: "44444444-4444-4444-8444-444444444444",
          },
        ],
        ok: true,
      });
    });

    expect(screen.queryByText("Ana")).toBeNull();
    expect(screen.getByText("Bia")).toBeVisible();
    expect(gateway.list).toHaveBeenCalledTimes(3);
    expect(gateway.list).toHaveBeenLastCalledWith(
      secondCompanyId,
      expect.anything(),
    );
  });

  it("does not query a company removed by bootstrap refresh", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    const getBootstrap = vi
      .fn()
      .mockResolvedValueOnce(snapshot())
      .mockResolvedValueOnce(snapshot(false));
    renderPage({ gateway, sessionGateway: createSessionGateway(getBootstrap) });

    await selectCompany(user);
    await screen.findByText("Ana");
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );

    await waitFor(() => expect(getBootstrap).toHaveBeenCalledTimes(2));
    expect(gateway.list).toHaveBeenCalledTimes(1);
  });

  it("requires a safe session refresh after known creation cannot refresh bootstrap", async () => {
    const user = userEvent.setup();
    const manualRefresh = deferred<ReturnType<typeof snapshot>>();
    const gateway = createGateway();
    const getBootstrap = vi
      .fn()
      .mockResolvedValueOnce(snapshot())
      .mockResolvedValueOnce(null)
      .mockReturnValueOnce(manualRefresh.promise);
    renderPage({ gateway, sessionGateway: createSessionGateway(getBootstrap) });

    await selectCompany(user);
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );

    expect(
      await screen.findByText(
        "O cadastro foi concluído, mas a sessão precisa ser atualizada.",
      ),
    ).toBeVisible();
    expect(
      screen.getByRole("button", { name: "Atualizar sessão" }),
    ).toBeVisible();
    expect(
      screen.queryByRole("button", { name: "Cadastrar funcionário" }),
    ).toBeNull();
    await user.click(screen.getByRole("button", { name: "Atualizar sessão" }));
    manualRefresh.resolve(snapshot());
    await waitFor(() => expect(getBootstrap).toHaveBeenCalledTimes(3));
    expect(
      await screen.findByRole("button", { name: "Cadastrar funcionário" }),
    ).toBeVisible();
    await waitFor(() => expect(gateway.list).toHaveBeenCalledTimes(2));
  });

  it("does not expose confirmed recovery state to a different user", async () => {
    const user = userEvent.setup();
    const manualRefresh = deferred<ReturnType<typeof snapshot>>();
    const getBootstrap = vi
      .fn()
      .mockResolvedValueOnce(snapshot())
      .mockResolvedValueOnce(null)
      .mockReturnValueOnce(manualRefresh.promise);
    renderPage({ sessionGateway: createSessionGateway(getBootstrap) });

    await selectCompany(user);
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );
    await screen.findByText(
      "O cadastro foi concluído, mas a sessão precisa ser atualizada.",
    );
    await user.click(screen.getByRole("button", { name: "Atualizar sessão" }));
    await act(async () => manualRefresh.resolve(snapshot(true, "user-2")));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(
      screen.queryByText(
        "O cadastro foi concluído, mas a sessão precisa ser atualizada.",
      ),
    ).toBeNull();
    await user.click(await waitForAuthenticatedDirectory());
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByLabelText("Nome")).toHaveValue("");
    expect(within(dialog).getByLabelText("E-mail")).toHaveValue("");
    expect(
      muiSelectValue(within(dialog).getByRole("combobox", { name: "Empresa" })),
    ).toBe("");
  });

  it.each(["employee_conflict"] as const)(
    "uses generic non-enumerating feedback for %s",
    async (kind) => {
      const user = userEvent.setup();
      const gateway = createGateway({
        create: vi.fn().mockResolvedValue({ error: { kind }, ok: false }),
      });
      renderPage({ gateway });

      await selectCompany(user);
      await fillValidForm(user);
      await user.click(
        screen.getByRole("button", { name: "Cadastrar funcionário" }),
      );

      expect(
        await screen.findByText("Não foi possível concluir o cadastro."),
      ).toBeVisible();
      expect(screen.queryByText("nova@example.test")).toBeNull();
    },
  );

  it.each(["unauthorized", "forbidden"] as const)(
    "neutralizes a delayed create %s before reconciling a capability change",
    async (kind) => {
      const user = userEvent.setup();
      const refresh = deferred<ReturnType<typeof snapshot>>();
      const gateway = createGateway({
        create: vi.fn().mockResolvedValue({ error: { kind }, ok: false }),
      });
      const getBootstrap = vi
        .fn()
        .mockResolvedValueOnce(snapshot())
        .mockReturnValueOnce(refresh.promise);
      renderPage({
        gateway,
        sessionGateway: createSessionGateway(getBootstrap),
      });

      await selectCompany(user);
      await screen.findByText("Ana");
      await fillValidForm(user);
      await user.click(
        screen.getByRole("button", { name: "Cadastrar funcionário" }),
      );

      expect(await screen.findByLabelText("Atualizando pessoas")).toBeVisible();
      expect(screen.queryByLabelText("Nome")).toBeNull();
      expect(gateway.create).toHaveBeenCalledTimes(1);
      refresh.resolve(snapshotForCompanies([secondCompanyId]));

      await screen.findByLabelText("Empresa");
      expect(screen.getByLabelText("Nome")).toHaveValue("");
      expect(screen.getByLabelText("E-mail")).toHaveValue("");
      expect(gateway.list).toHaveBeenCalledTimes(2);
      expect(
        screen.queryByText("Não foi possível concluir o cadastro."),
      ).toBeNull();
    },
  );

  it.each(["unauthorized", "forbidden"] as const)(
    "neutralizes a delayed list %s before reconciling a user change",
    async (kind) => {
      const user = userEvent.setup();
      const refresh = deferred<ReturnType<typeof snapshot>>();
      let listAttempts = 0;
      const gateway = createGateway({
        list: vi.fn().mockImplementation(async () => {
          listAttempts += 1;
          return listAttempts === 1
            ? { error: { kind }, ok: false }
            : {
                items: [
                  {
                    displayName: "Ana",
                    employeeId: "33333333-3333-4333-8333-333333333333",
                    membershipId: "44444444-4444-4444-8444-444444444444",
                  },
                ],
                ok: true,
              };
        }),
      });
      const getBootstrap = vi
        .fn()
        .mockResolvedValueOnce(snapshot())
        .mockReturnValueOnce(refresh.promise);
      renderPage({
        gateway,
        sessionGateway: createSessionGateway(getBootstrap),
      });

      expect(await screen.findByLabelText("Atualizando pessoas")).toBeVisible();
      expect(screen.queryByLabelText("Nome")).toBeNull();
      expect(gateway.list).toHaveBeenCalledTimes(1);
      await act(async () => refresh.resolve(snapshot(true, "user-2")));

      await user.click(
        await screen.findByRole("button", { name: "Cadastrar funcionário" }),
      );
      const dialog = await screen.findByRole("dialog");
      expect(within(dialog).getByLabelText("Nome")).toHaveValue("");
      expect(within(dialog).getByLabelText("E-mail")).toHaveValue("");
      expect(gateway.list).toHaveBeenCalledTimes(2);
      expect(gateway.create).not.toHaveBeenCalled();
    },
  );

  it("opens the read-only employee profile dialog from the directory link", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    renderPage({ gateway });

    await selectCompany(user);
    const employee = await screen.findByRole("button", { name: /Ana/ });
    await user.click(employee);

    const dialog = await screen.findByRole("dialog");
    expect(dialog).toBeVisible();
    expect(within(dialog).getByText("Ana")).toBeVisible();
    expect(
      within(dialog).queryByText("33333333-3333-4333-8333-333333333333"),
    ).toBeNull();
    expect(
      within(dialog).queryByText("44444444-4444-4444-8444-444444444444"),
    ).toBeNull();
    expect(
      within(dialog).getByRole("group", { name: "Empresa Alfa" }),
    ).toBeVisible();
  });

  it("drops a delayed create after closing the dialog", async () => {
    const user = userEvent.setup();
    let resolveCreate:
      | ((result: EmployeeDirectoryCreateResult) => void)
      | undefined;
    const gateway = createGateway({
      create: vi.fn(
        () =>
          new Promise<EmployeeDirectoryCreateResult>((resolve) => {
            resolveCreate = resolve;
          }),
      ),
    });
    renderPage({ gateway });
    await screen.findByText("Ana");
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Cadastrar funcionário" }),
    );
    await user.click(
      within(screen.getByRole("dialog")).getByRole("button", {
        name: "Fechar",
      }),
    );
    resolveCreate?.({ ok: true, state: "created" });
    await waitFor(() => expect(gateway.create).toHaveBeenCalledTimes(1));
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Cadastrar funcionário" }),
      ).toBeEnabled(),
    );

    await user.click(
      await screen.findByRole("button", { name: "Cadastrar funcionário" }),
    );
    const dialog = await screen.findByRole("dialog");
    await user.type(within(dialog).getByLabelText("Nome"), "Cadastro atual");
    await user.type(
      within(dialog).getByLabelText("E-mail"),
      "atual@example.test",
    );
    resolveCreate?.({ ok: true, state: "created" });
    await waitFor(() => expect(gateway.create).toHaveBeenCalledTimes(1));
    expect(screen.getByRole("dialog")).toBeVisible();
    expect(screen.queryByText("Funcionário cadastrado.")).toBeNull();
  });
});
