import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import type { MockSessionGateway } from "../auth/session-contract";
import { SessionProvider } from "../auth/session-provider";
import type {
  PlatformOrganizationGateway,
  PlatformOrganizationProvisionResult,
} from "./platform-organization-client";
import { PlatformOrganizationPage } from "./platform-organization-page";

const fixedRequestId = "77777777-7777-4777-8777-777777777777";

function createGateway(
  overrides: Partial<PlatformOrganizationGateway> = {},
): PlatformOrganizationGateway {
  return {
    provision: vi.fn().mockResolvedValue({
      ok: true,
      state: "created",
      invitationStatus: "pending_delivery",
    }),
    ...overrides,
  };
}

function snapshot(isPlatformOperator: boolean, userId = "user-1") {
  return {
    companies: [],
    flags: {},
    platform: { isPlatformOperator },
    user: { displayName: "Operador", id: userId },
  } as const;
}

function deferred<T>() {
  let resolve: (value: T) => void = () => undefined;
  const promise = new Promise<T>((nextResolve) => {
    resolve = nextResolve;
  });
  return { promise, resolve };
}

function createSessionGateway(
  getBootstrap = vi.fn().mockResolvedValue(snapshot(true)),
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
  gateway?: PlatformOrganizationGateway;
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
          <PlatformOrganizationPage
            gateway={gateway}
            {...(createRequestId ? { createRequestId } : {})}
          />
        </SessionProvider>
      </QueryClientProvider>
    </VinxThemeProvider>,
  );

  return { gateway, sessionGateway };
}

function submitFormProgrammatically(): void {
  const form = screen.getByLabelText("Nome do grupo").closest("form");
  if (!form) throw new Error("form not found");
  fireEvent.submit(form);
}

async function fillValidForm(user: ReturnType<typeof userEvent.setup>) {
  await screen.findByLabelText("Nome do grupo");
  await user.type(screen.getByLabelText("Nome do grupo"), "Grupo Northwind");
  await user.type(screen.getByLabelText("Nome da empresa"), "Northwind HQ");
  await user.type(screen.getByLabelText("Nome do dono"), "Owner");
  await user.type(
    screen.getByLabelText("E-mail do dono"),
    "owner@example.test",
  );
}

async function submit(user: ReturnType<typeof userEvent.setup>): Promise<void> {
  const submitButton = await screen.findByRole("button", {
    name: "Criar organização",
  });
  await user.click(submitButton);
}

describe("PlatformOrganizationPage", () => {
  it("renders an inert notice for non-operators without calling the gateway", async () => {
    const gateway = createGateway();
    renderPage({
      gateway,
      sessionGateway: createSessionGateway(
        vi.fn().mockResolvedValue(snapshot(false)),
      ),
    });

    expect(
      await screen.findByText(
        "Esta área está disponível apenas para operadores de plataforma.",
      ),
    ).toBeVisible();
    expect(screen.queryByLabelText("Nome do grupo")).toBeNull();
    expect(gateway.provision).not.toHaveBeenCalled();
  });

  it("creates an organization and publishes only the allow-listed receipt", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    renderPage({
      createRequestId: () => fixedRequestId,
      gateway,
    });

    await fillValidForm(user);
    await submit(user);

    expect(
      await screen.findByText(
        "Organização criada. Convite pendente de entrega.",
      ),
    ).toBeVisible();
    expect(gateway.provision).toHaveBeenCalledTimes(1);
    expect(gateway.provision).toHaveBeenCalledWith({
      companyName: "Northwind HQ",
      groupName: "Grupo Northwind",
      ownerDisplayName: "Owner",
      ownerEmail: "owner@example.test",
      requestId: fixedRequestId,
    });
    expect(screen.queryByText(/owner@example\.test/)).toBeNull();
    expect(document.querySelector("img")).toBeNull();
    expect(screen.getByLabelText("Nome do grupo")).toHaveValue("");
  });

  it("validates locally before provisioning", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    renderPage({ gateway });

    await submit(user);

    expect(await screen.findAllByText("Informe um nome válido.")).toHaveLength(
      3,
    );
    expect(
      await screen.findAllByText("Informe um e-mail válido."),
    ).toHaveLength(1);
    expect(gateway.provision).not.toHaveBeenCalled();
  });

  it("double-click and re-render produce a single attempt", async () => {
    const user = userEvent.setup();
    const provision = deferred<PlatformOrganizationProvisionResult>();
    const gateway = createGateway({
      provision: vi.fn().mockReturnValue(provision.promise),
    });
    renderPage({ gateway });

    await fillValidForm(user);
    await submit(user);
    fireEvent.click(screen.getByRole("button", { name: "Criar organização" }));
    submitFormProgrammatically();

    await waitFor(() => expect(gateway.provision).toHaveBeenCalledTimes(1));
    provision.resolve({
      invitationStatus: "pending_delivery",
      ok: true,
      state: "created",
    });
    expect(
      await screen.findByText(
        "Organização criada. Convite pendente de entrega.",
      ),
    ).toBeVisible();
    expect(gateway.provision).toHaveBeenCalledTimes(1);
  });

  it("replay updates the same single receipt", async () => {
    const user = userEvent.setup();
    const gateway = createGateway({
      provision: vi.fn().mockResolvedValue({
        invitationStatus: "pending_delivery",
        ok: true,
        state: "replay",
      }),
    });
    renderPage({ gateway });

    await fillValidForm(user);
    await submit(user);

    expect(
      await screen.findByText(
        "Organização já havia sido criada. Convite pendente de entrega.",
      ),
    ).toBeVisible();
    expect(
      screen.queryByText("Organização criada. Convite pendente de entrega."),
    ).toBeNull();
    expect(gateway.provision).toHaveBeenCalledTimes(1);
  });

  it.each([["organization_conflict"], ["idempotency_conflict"]] as const)(
    "%s shows a generic conflict message without success or hidden retry",
    async (kind) => {
      const user = userEvent.setup();
      const gateway = createGateway({
        provision: vi.fn().mockResolvedValue({
          error: { kind, status: 409 },
          ok: false,
        }),
      });
      renderPage({ gateway });

      await fillValidForm(user);
      await submit(user);

      expect(
        await screen.findByText(
          "Não foi possível concluir: já existe um registro conflitante.",
        ),
      ).toBeVisible();
      expect(
        screen.queryByText("Organização criada. Convite pendente de entrega."),
      ).toBeNull();
      expect(gateway.provision).toHaveBeenCalledTimes(1);
    },
  );

  it("rate limiting shows a waiting message without success or hidden retry", async () => {
    const user = userEvent.setup();
    const gateway = createGateway({
      provision: vi.fn().mockResolvedValue({
        error: { kind: "rate_limited", status: 429 },
        ok: false,
      }),
    });
    renderPage({ gateway });

    await fillValidForm(user);
    await submit(user);

    expect(
      await screen.findByText(
        "Muitas tentativas. Aguarde antes de tentar novamente.",
      ),
    ).toBeVisible();
    expect(
      screen.queryByText("Organização criada. Convite pendente de entrega."),
    ).toBeNull();
    expect(gateway.provision).toHaveBeenCalledTimes(1);
  });

  it("locks the form after an ambiguous result and retries explicitly with the same request", async () => {
    const user = userEvent.setup();
    const provision = vi
      .fn<PlatformOrganizationGateway["provision"]>()
      .mockResolvedValueOnce({ error: { kind: "network" }, ok: false })
      .mockResolvedValueOnce({
        invitationStatus: "pending_delivery",
        ok: true,
        state: "created",
      });
    const gateway = createGateway({ provision });
    renderPage({ createRequestId: () => fixedRequestId, gateway });

    await fillValidForm(user);
    await submit(user);

    expect(
      await screen.findByText(
        "A tentativa anterior terminou sem resposta confirmada. Tente novamente com os mesmos dados antes de criar outra organização.",
      ),
    ).toBeVisible();
    const submitButton = screen.getByRole("button", {
      name: "Criar organização",
    });
    expect(submitButton).toBeDisabled();
    expect(screen.getByLabelText("Nome do grupo")).toBeDisabled();
    submitFormProgrammatically();
    expect(gateway.provision).toHaveBeenCalledTimes(1);

    await user.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(
      await screen.findByText(
        "Organização criada. Convite pendente de entrega.",
      ),
    ).toBeVisible();
    expect(gateway.provision).toHaveBeenCalledTimes(2);
    expect(provision.mock.calls[1]?.[0]).toEqual(provision.mock.calls[0]?.[0]);
  });

  it("keeps the attempt after a malformed success and publishes one receipt on the replay retry", async () => {
    const user = userEvent.setup();
    const provision = vi
      .fn<PlatformOrganizationGateway["provision"]>()
      .mockResolvedValueOnce({
        error: { kind: "protocol", mayHaveCommitted: true, status: 201 },
        ok: false,
      })
      .mockResolvedValueOnce({
        invitationStatus: "pending_delivery",
        ok: true,
        state: "replay",
      });
    const gateway = createGateway({ provision });
    renderPage({ createRequestId: () => fixedRequestId, gateway });

    await fillValidForm(user);
    await submit(user);

    expect(
      await screen.findByText(
        "A tentativa anterior terminou sem resposta confirmada. Tente novamente com os mesmos dados antes de criar outra organização.",
      ),
    ).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(
      await screen.findByText(
        "Organização já havia sido criada. Convite pendente de entrega.",
      ),
    ).toBeVisible();
    expect(provision.mock.calls[1]?.[0]).toEqual(provision.mock.calls[0]?.[0]);
  });

  it("blocks provisioning terminally after a step-up denial", async () => {
    const user = userEvent.setup();
    const provision = vi
      .fn<PlatformOrganizationGateway["provision"]>()
      .mockResolvedValue({
        error: { kind: "mfa_assurance_required", status: 403 },
        ok: false,
      });
    const gateway = createGateway({ provision });
    renderPage({ gateway });

    await fillValidForm(user);
    await submit(user);

    expect(
      await screen.findByText(
        "Esta ação exige autenticação com segundo fator. Saia e entre novamente com MFA para continuar.",
      ),
    ).toBeVisible();
    const submitButton = screen.getByRole("button", {
      name: "Criar organização",
    });
    expect(submitButton).toBeDisabled();
    expect(screen.getByLabelText("Nome do grupo")).toBeDisabled();
    fireEvent.click(submitButton);
    submitFormProgrammatically();
    expect(gateway.provision).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("button", { name: "Sair" })).toBeVisible();
    expect(
      screen.queryByText("Organização criada. Convite pendente de entrega."),
    ).toBeNull();
  });

  it("refreshes the session after an authorization denial", async () => {
    const user = userEvent.setup();
    const getBootstrap = vi
      .fn()
      .mockResolvedValueOnce(snapshot(true))
      .mockResolvedValueOnce(snapshot(true));
    const gateway = createGateway({
      provision: vi.fn().mockResolvedValue({
        error: { kind: "platform_operator_required", status: 403 },
        ok: false,
      }),
    });
    renderPage({
      gateway,
      sessionGateway: createSessionGateway(getBootstrap),
    });

    await fillValidForm(user);
    await submit(user);

    expect(
      await screen.findByText(
        "Sua sessão não permite mais esta ação. Entre novamente.",
      ),
    ).toBeVisible();
    await waitFor(() => expect(getBootstrap).toHaveBeenCalledTimes(2));
    expect(
      screen.queryByText("Organização criada. Convite pendente de entrega."),
    ).toBeNull();
  });

  it("removes the administrative content when the operator gate is revoked", async () => {
    const user = userEvent.setup();
    const refresh = deferred<ReturnType<typeof snapshot>>();
    const getBootstrap = vi
      .fn()
      .mockResolvedValueOnce(snapshot(true))
      .mockReturnValueOnce(refresh.promise);
    const gateway = createGateway();
    renderPage({
      gateway,
      sessionGateway: createSessionGateway(getBootstrap),
    });

    await fillValidForm(user);
    window.dispatchEvent(new Event("online"));
    refresh.resolve(snapshot(false));

    expect(
      await screen.findByText(
        "Esta área está disponível apenas para operadores de plataforma.",
      ),
    ).toBeVisible();
    expect(screen.queryByLabelText("Nome do grupo")).toBeNull();
    expect(gateway.provision).not.toHaveBeenCalled();
  });

  it("produces no receipt and no retry when the operator identity swaps mid-flight", async () => {
    const user = userEvent.setup();
    const provision = deferred<PlatformOrganizationProvisionResult>();
    const refresh = deferred<ReturnType<typeof snapshot>>();
    const getBootstrap = vi
      .fn()
      .mockResolvedValueOnce(snapshot(true))
      .mockReturnValueOnce(refresh.promise);
    const gateway = createGateway({
      provision: vi.fn().mockReturnValue(provision.promise),
    });
    renderPage({
      gateway,
      sessionGateway: createSessionGateway(getBootstrap),
    });

    await fillValidForm(user);
    await submit(user);
    await waitFor(() => expect(gateway.provision).toHaveBeenCalledTimes(1));
    window.dispatchEvent(new Event("online"));
    refresh.resolve(snapshot(true, "user-2"));
    provision.resolve({
      invitationStatus: "pending_delivery",
      ok: true,
      state: "created",
    });

    await waitFor(() =>
      expect(screen.getByLabelText("Nome do grupo")).toHaveValue(""),
    );
    expect(
      screen.queryByText("Organização criada. Convite pendente de entrega."),
    ).toBeNull();
    expect(
      screen.queryByRole("button", { name: "Tentar novamente" }),
    ).toBeNull();
    expect(gateway.provision).toHaveBeenCalledTimes(1);
  });
});
