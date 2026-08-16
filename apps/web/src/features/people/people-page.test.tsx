import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
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
    list: vi.fn().mockResolvedValue({
      items: [
        {
          displayName: "Ana",
          employeeId: "33333333-3333-4333-8333-333333333333",
          membershipId: "44444444-4444-4444-8444-444444444444",
        },
      ],
      ok: true,
    }),
    ...overrides,
  };
}

function snapshot(capability = true, userId = "user-1") {
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
        ]
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
  const current = snapshot(true, userId);
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
  await user.click(await screen.findByLabelText("Empresa"));
  await user.click(await screen.findByRole("option", { name: "Alfa" }));
}

async function selectSecondCompany(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByLabelText("Empresa"));
  await user.click(await screen.findByRole("option", { name: "Beta" }));
}

async function fillValidForm(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText("Nome"), "Nova Pessoa");
  await user.type(screen.getByLabelText("E-mail"), "nova@example.test");
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

  it("waits for explicit selection and only renders display names as text", async () => {
    const user = userEvent.setup();
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
    renderPage({ gateway });

    await screen.findByLabelText("Empresa");
    expect(gateway.list).not.toHaveBeenCalled();

    await selectCompany(user);
    await waitFor(() =>
      expect(gateway.list).toHaveBeenCalledWith(companyId, expect.anything()),
    );
    expect(
      await screen.findByText("<img src=x onerror=alert(1)>"),
    ).toBeVisible();
    expect(document.querySelector("img")).toBeNull();
    expect(
      screen.queryByText("33333333-3333-4333-8333-333333333333"),
    ).toBeNull();
  });

  it("validates locally before reserving", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    renderPage({ gateway });

    await selectCompany(user);
    await user.click(
      screen.getByRole("button", { name: "Reservar funcionário" }),
    );

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
      screen.getByRole("button", { name: "Reservar funcionário" }),
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
    expect(await screen.findByText("Funcionário reservado.")).toBeVisible();
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
      screen.getByRole("button", { name: "Reservar funcionário" }),
    );
    expect(screen.queryByText("Funcionário reservado.")).toBeNull();

    refresh.resolve(snapshot());

    expect(await screen.findByText("Funcionário reservado.")).toBeVisible();
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
      screen.getByRole("button", { name: "Reservar funcionário" }),
    );
    refresh.resolve(snapshot(false));

    expect(
      await screen.findByText(
        "Nenhuma empresa está disponível para esta ação.",
      ),
    ).toBeVisible();
    expect(gateway.list).toHaveBeenCalledTimes(1);
    expect(screen.queryByText("Funcionário reservado.")).toBeNull();
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
      screen.getByRole("button", { name: "Reservar funcionário" }),
    );
    refresh.resolve(snapshot(true, "user-2"));

    await screen.findByLabelText("Empresa");
    await waitFor(() => expect(gateway.list).toHaveBeenCalledTimes(1));
    expect(screen.queryByText("Funcionário reservado.")).toBeNull();
    expect(screen.getByLabelText("Nome")).toHaveValue("");
    expect(screen.getByLabelText("E-mail")).toHaveValue("");
    expect(
      screen.getByRole("combobox", { name: "Empresa" }),
    ).not.toHaveTextContent("Alfa");
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
    await user.click(
      screen.getByRole("button", { name: "Reservar funcionário" }),
    );
    refresh.resolve(snapshotForCompanies([secondCompanyId]));

    await screen.findByLabelText("Empresa");
    expect(screen.getByLabelText("Nome")).toHaveValue("");
    expect(screen.getByLabelText("E-mail")).toHaveValue("");
    expect(
      screen.getByRole("combobox", { name: "Empresa" }),
    ).not.toHaveTextContent("Alfa");
    await user.click(screen.getByRole("combobox", { name: "Empresa" }));
    expect(await screen.findByRole("option", { name: "Beta" })).toBeVisible();
  });

  it("clears typed unselected fields before rendering a different user view", async () => {
    const user = userEvent.setup();
    const refresh = deferred<ReturnType<typeof snapshot>>();
    const getBootstrap = vi
      .fn()
      .mockResolvedValueOnce(snapshot())
      .mockReturnValueOnce(refresh.promise);
    renderPage({ sessionGateway: createSessionGateway(getBootstrap) });

    await screen.findByLabelText("Nome");
    await fillValidForm(user);
    window.dispatchEvent(new Event("online"));
    refresh.resolve(snapshot(true, "user-2"));

    await screen.findByLabelText("Nome");
    expect(screen.getByLabelText("Nome")).toHaveValue("");
    expect(screen.getByLabelText("E-mail")).toHaveValue("");
    expect(
      screen.getByRole("combobox", { name: "Empresa" }),
    ).not.toHaveTextContent("Alfa");
  });

  it("uses a bound default request ID factory", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    renderPage({ gateway, createRequestId: undefined });

    await selectCompany(user);
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Reservar funcionário" }),
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
      screen.getByRole("button", { name: "Reservar funcionário" }),
    );
    expect(
      await screen.findByRole("button", { name: "Tentar novamente" }),
    ).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Tentar novamente" }));

    await waitFor(() => expect(gateway.create).toHaveBeenCalledTimes(2));
    expect(command.mock.calls[0][0]).toEqual(command.mock.calls[1][0]);
  });

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
    renderPage({ gateway });

    await selectCompany(user);
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Reservar funcionário" }),
    );

    expect(gateway.create).toHaveBeenCalledTimes(1);
    expect(
      screen.getByRole("button", { name: "Reservar funcionário" }),
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
    renderPage({ gateway });

    await selectCompany(user);
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Reservar funcionário" }),
    );
    await selectSecondCompany(user);
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
    renderPage({ gateway });

    await selectCompany(user);
    await fillValidForm(user);
    await user.click(
      screen.getByRole("button", { name: "Reservar funcionário" }),
    );
    await selectSecondCompany(user);
    await user.clear(screen.getByLabelText("Nome"));
    await user.type(screen.getByLabelText("Nome"), "Pessoa atual");
    resolveCreate?.({ ok: true, state: "created" });

    await waitFor(() => expect(gateway.list).toHaveBeenCalledTimes(2));
    expect(screen.getByLabelText("Nome")).toHaveValue("Pessoa atual");
    expect(screen.queryByText("Funcionário reservado.")).toBeNull();
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
      screen.getByRole("button", { name: "Reservar funcionário" }),
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
      screen.getByRole("button", { name: "Reservar funcionário" }),
    );

    expect(
      await screen.findByText(
        "A reserva foi concluída, mas a sessão precisa ser atualizada.",
      ),
    ).toBeVisible();
    expect(
      screen.getByRole("button", { name: "Atualizar sessão" }),
    ).toBeVisible();
    expect(
      screen.queryByRole("button", { name: "Reservar funcionário" }),
    ).toBeNull();
    await user.click(screen.getByRole("button", { name: "Atualizar sessão" }));
    manualRefresh.resolve(snapshot());
    await waitFor(() => expect(getBootstrap).toHaveBeenCalledTimes(3));
    expect(
      await screen.findByRole("button", { name: "Reservar funcionário" }),
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
      screen.getByRole("button", { name: "Reservar funcionário" }),
    );
    await screen.findByText(
      "A reserva foi concluída, mas a sessão precisa ser atualizada.",
    );
    await user.click(screen.getByRole("button", { name: "Atualizar sessão" }));
    manualRefresh.resolve(snapshot(true, "user-2"));

    await screen.findByLabelText("Empresa");
    expect(
      screen.queryByText(
        "A reserva foi concluída, mas a sessão precisa ser atualizada.",
      ),
    ).toBeNull();
    expect(screen.getByLabelText("Nome")).toHaveValue("");
    expect(screen.getByLabelText("E-mail")).toHaveValue("");
    expect(
      screen.getByRole("combobox", { name: "Empresa" }),
    ).not.toHaveTextContent("Alfa");
  });

  it.each(["employee_conflict", "unauthorized", "forbidden"] as const)(
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
        screen.getByRole("button", { name: "Reservar funcionário" }),
      );

      expect(
        await screen.findByText("Não foi possível concluir esta reserva."),
      ).toBeVisible();
      expect(screen.queryByText("nova@example.test")).toBeNull();
    },
  );

  it.each(["unauthorized", "forbidden"] as const)(
    "clears the selected directory and revalidates bootstrap after %s",
    async (kind) => {
      const user = userEvent.setup();
      const gateway = createGateway({
        create: vi.fn().mockResolvedValue({ error: { kind }, ok: false }),
      });
      const getBootstrap = vi
        .fn()
        .mockResolvedValueOnce(snapshot())
        .mockResolvedValueOnce(snapshot());
      renderPage({
        gateway,
        sessionGateway: createSessionGateway(getBootstrap),
      });

      await selectCompany(user);
      await screen.findByText("Ana");
      await fillValidForm(user);
      await user.click(
        screen.getByRole("button", { name: "Reservar funcionário" }),
      );

      await waitFor(() => expect(getBootstrap).toHaveBeenCalledTimes(2));
      await waitFor(() => expect(gateway.list).toHaveBeenCalledTimes(2));
      expect(
        await screen.findByText("Não foi possível concluir esta reserva."),
      ).toBeVisible();
    },
  );
});
