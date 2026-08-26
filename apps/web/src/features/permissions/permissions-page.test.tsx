import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { FeedbackProvider } from "../../shared/ui/feedback-provider";
import type { MockSessionGateway } from "../auth/session-contract";
import { SessionProvider } from "../auth/session-provider";
import type {
  PermissionsAssignmentCommand,
  PermissionsGateway,
  PermissionsMembersResult,
  PermissionsMutationResult,
  PermissionsRolesResult,
} from "./permissions-client";
import { PermissionsPage } from "./permissions-page";

const fixedRequestId = "77777777-7777-4777-8777-777777777777";
const companyId = "11111111-1111-4111-8111-111111111111";
const membershipAna = "aaaaaaaa-bbbb-4bbb-8bbb-aaaaaaaaaaaa";
const membershipBruno = "bbbbbbbb-cccc-4ccc-8ccc-bbbbbbbbbbbb";
const roleAdminId = "22222222-3333-4333-8333-222222222222";
const roleViewerId = "33333333-4444-4555-8666-333333333333";

function membersList(): PermissionsMembersResult {
  return {
    items: [
      {
        capabilities: ["foundation.roles.assign"],
        displayName: "Ana Souza",
        invitationStatus: null,
        membershipId: membershipAna,
        roles: [{ name: "Administrador", roleId: roleAdminId }],
      },
      {
        capabilities: [],
        displayName: "Bruno Lima",
        invitationStatus: "pending_delivery",
        membershipId: membershipBruno,
        roles: [],
      },
    ],
    ok: true,
  };
}

function rolesList(): PermissionsRolesResult {
  return {
    items: [
      { capabilities: [], name: "Administrador", roleId: roleAdminId },
      { capabilities: [], name: "Leitor", roleId: roleViewerId },
    ],
    ok: true,
  };
}

function createGateway(
  overrides: Partial<PermissionsGateway> = {},
): PermissionsGateway {
  return {
    assignRole: vi.fn().mockResolvedValue({ ok: true }),
    copyPermissions: vi.fn().mockResolvedValue({ ok: true }),
    inviteMember: vi
      .fn()
      .mockResolvedValue({ ok: true, state: "created" as const }),
    listMembers: vi.fn().mockResolvedValue(membersList()),
    listRoles: vi.fn().mockResolvedValue(rolesList()),
    unassignRole: vi.fn().mockResolvedValue({ ok: true }),
    ...overrides,
  };
}

function snapshot(capability = true, userId = "user-1") {
  return {
    companies: capability
      ? [
          {
            capabilities: [
              { name: "foundation.roles.assign", scopes: ["Company"] },
            ],
            companyId,
            companyName: "Alfa",
            membershipId: "55555555-5555-4555-8555-555555555555",
          },
        ]
      : [],
    flags: {},
    platform: { isPlatformOperator: false },
    user: { displayName: "Maria", id: userId },
  } as const;
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
  gateway?: PermissionsGateway;
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
          <FeedbackProvider>
            <PermissionsPage
              gateway={gateway}
              {...(createRequestId ? { createRequestId } : {})}
            />
          </FeedbackProvider>
        </SessionProvider>
      </QueryClientProvider>
    </VinxThemeProvider>,
  );

  return { gateway, sessionGateway };
}

async function selectCompany(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByLabelText("Empresa"));
  await user.click(await screen.findByRole("option", { name: "Alfa" }));
}

async function openRolesDialog(
  user: ReturnType<typeof userEvent.setup>,
  memberName = "Ana Souza",
) {
  await user.click(
    await screen.findByRole("button", { name: `Papéis de ${memberName}` }),
  );
}

async function removeRoleViaChip(
  user: ReturnType<typeof userEvent.setup>,
  chipName: string,
) {
  const chip = await screen.findByRole("button", { name: chipName });
  await user.type(chip, "{Backspace}");
}

describe("PermissionsPage", () => {
  it("renders an inert notice without gateway calls when no company is eligible", async () => {
    const gateway = createGateway();
    renderPage({
      gateway,
      sessionGateway: createSessionGateway(
        vi.fn().mockResolvedValue(snapshot(false)),
      ),
    });

    expect(
      await screen.findByText(
        "Nenhuma empresa disponível para gerenciar permissões.",
      ),
    ).toBeVisible();
    expect(gateway.listMembers).not.toHaveBeenCalled();
    expect(gateway.listRoles).not.toHaveBeenCalled();
    expect(gateway.inviteMember).not.toHaveBeenCalled();
  });

  it("lists members with role and invitation chips after explicit selection", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    renderPage({ gateway });

    await selectCompany(user);

    expect(await screen.findByText("Ana Souza")).toBeVisible();
    expect(await screen.findByText("Bruno Lima")).toBeVisible();
    expect(screen.getByText("Administrador")).toBeVisible();
    expect(screen.getByText("Convite pendente")).toBeVisible();
    expect(gateway.listMembers).toHaveBeenCalledWith(
      companyId,
      expect.anything(),
    );
    expect(gateway.listRoles).toHaveBeenCalledWith(
      companyId,
      expect.anything(),
    );
  });

  it("maps a failed invitation delivery to a warning chip", async () => {
    const user = userEvent.setup();
    const members = membersList();
    if (members.ok) {
      members.items[1].invitationStatus = "provider_failed";
    }
    renderPage({
      gateway: createGateway({
        listMembers: vi.fn().mockResolvedValue(members),
      }),
    });

    await selectCompany(user);

    expect(await screen.findByText("Falha na entrega")).toBeVisible();
  });

  it("maps a delivered invitation awaiting acceptance to a neutral chip", async () => {
    const user = userEvent.setup();
    const members = membersList();
    if (members.ok) {
      members.items[1].invitationStatus = "pending_acceptance";
    }
    renderPage({
      gateway: createGateway({
        listMembers: vi.fn().mockResolvedValue(members),
      }),
    });

    await selectCompany(user);

    expect(await screen.findByText("Convite enviado")).toBeVisible();
    expect(screen.queryByText("Falha na entrega")).toBeNull();
  });

  it("shows the empty members state after selection", async () => {
    const user = userEvent.setup();
    renderPage({
      gateway: createGateway({
        listMembers: vi.fn().mockResolvedValue({ items: [], ok: true }),
      }),
    });

    await selectCompany(user);

    expect(await screen.findByText("Nenhum membro na empresa.")).toBeVisible();
  });

  it("shows a read failure alert with an explicit retry", async () => {
    const user = userEvent.setup();
    const listMembers = vi
      .fn<PermissionsGateway["listMembers"]>()
      .mockResolvedValueOnce({
        error: { kind: "unavailable", status: 503 },
        ok: false,
      })
      .mockResolvedValueOnce(membersList());
    renderPage({
      gateway: createGateway({ listMembers }),
    });

    await selectCompany(user);

    expect(
      await screen.findByText("Não foi possível carregar os membros."),
    ).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(await screen.findByText("Ana Souza")).toBeVisible();
    expect(listMembers).toHaveBeenCalledTimes(2);
  });

  it("unassigns a role from the member chip with a fresh request id", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    renderPage({ createRequestId: () => fixedRequestId, gateway });

    await selectCompany(user);
    await removeRoleViaChip(user, "Remover papel Administrador de Ana Souza");

    await waitFor(() => expect(gateway.unassignRole).toHaveBeenCalledTimes(1));
    const command: PermissionsAssignmentCommand = {
      companyId,
      membershipId: membershipAna,
      requestId: fixedRequestId,
      roleId: roleAdminId,
    };
    expect(gateway.unassignRole).toHaveBeenCalledWith(command);
    expect(await screen.findByText("Papel removido.")).toBeVisible();
    await waitFor(() => expect(gateway.listMembers).toHaveBeenCalledTimes(2));
  });

  it("surfaces the last admin guard inline without retry", async () => {
    const user = userEvent.setup();
    const gateway = createGateway({
      unassignRole: vi.fn().mockResolvedValue({
        error: { kind: "last_admin_guard", status: 409 },
        ok: false,
      }),
    });
    renderPage({ gateway });

    await selectCompany(user);
    await removeRoleViaChip(user, "Remover papel Administrador de Ana Souza");

    expect(
      await screen.findByText(
        "A empresa precisa de ao menos um administrador ativo.",
      ),
    ).toBeVisible();
    expect(screen.queryByText("Papel removido.")).toBeNull();
    expect(
      screen.queryByRole("button", { name: "Tentar novamente" }),
    ).toBeNull();
  });

  it("assigns a role from the dialog and closes it on success", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    renderPage({ createRequestId: () => fixedRequestId, gateway });

    await selectCompany(user);
    await openRolesDialog(user);

    expect(
      await screen.findByRole("heading", { name: "Papéis de Ana Souza" }),
    ).toBeVisible();
    await user.click(screen.getByRole("combobox", { name: "Adicionar papel" }));
    await user.click(await screen.findByRole("option", { name: "Leitor" }));
    await user.click(screen.getByRole("button", { name: "Atribuir papel" }));

    await waitFor(() => expect(gateway.assignRole).toHaveBeenCalledTimes(1));
    expect(gateway.assignRole).toHaveBeenCalledWith({
      companyId,
      membershipId: membershipAna,
      requestId: fixedRequestId,
      roleId: roleViewerId,
    });
    expect(await screen.findByText("Papel atribuído.")).toBeVisible();
    expect(
      screen.queryByRole("heading", { name: "Papéis de Ana Souza" }),
    ).toBeNull();
  });

  it("copies permissions from another member, excluding the target itself", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    renderPage({ createRequestId: () => fixedRequestId, gateway });

    await selectCompany(user);
    await user.click(
      await screen.findByRole("button", {
        name: "Copiar de outro membro para Bruno Lima",
      }),
    );

    const sourceSelect = await screen.findByRole("combobox", {
      name: "Copiar de",
    });
    await user.click(sourceSelect);
    const options = await screen.findAllByRole("option");
    expect(options.map((option) => option.textContent)).toEqual(["Ana Souza"]);
    await user.click(screen.getByRole("option", { name: "Ana Souza" }));
    await user.click(screen.getByRole("button", { name: "Copiar permissões" }));

    await waitFor(() =>
      expect(gateway.copyPermissions).toHaveBeenCalledTimes(1),
    );
    expect(gateway.copyPermissions).toHaveBeenCalledWith({
      companyId,
      requestId: fixedRequestId,
      sourceMembershipId: membershipAna,
      targetMembershipId: membershipBruno,
    });
    expect(await screen.findByText("Permissões copiadas.")).toBeVisible();
  });

  it("validates the invite form locally before calling the gateway", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    renderPage({ gateway });

    await selectCompany(user);
    await user.click(
      await screen.findByRole("button", { name: "Convidar membro" }),
    );
    await user.click(screen.getByRole("button", { name: "Convidar" }));

    expect(await screen.findByText("Informe um nome válido.")).toBeVisible();
    expect(screen.getByText("Informe um e-mail válido.")).toBeVisible();
    expect(gateway.inviteMember).not.toHaveBeenCalled();
  });

  it("invites a member and never echoes the e-mail back after submit", async () => {
    const user = userEvent.setup();
    const gateway = createGateway();
    renderPage({ createRequestId: () => fixedRequestId, gateway });

    await selectCompany(user);
    await user.click(
      await screen.findByRole("button", { name: "Convidar membro" }),
    );
    await user.type(screen.getByLabelText("Nome"), "Carla Menezes");
    await user.type(screen.getByLabelText("E-mail"), "carla@example.test");
    await user.click(screen.getByRole("button", { name: "Convidar" }));

    await waitFor(() => expect(gateway.inviteMember).toHaveBeenCalledTimes(1));
    expect(gateway.inviteMember).toHaveBeenCalledWith({
      companyId,
      displayName: "Carla Menezes",
      email: "carla@example.test",
      requestId: fixedRequestId,
    });
    expect(await screen.findByText("Convite enviado.")).toBeVisible();
    expect(screen.queryByText(/carla@example\.test/)).toBeNull();
    expect(screen.getByLabelText("Nome")).toHaveValue("");
  });

  it("surfaces the member conflict inline in the invite dialog", async () => {
    const user = userEvent.setup();
    const gateway = createGateway({
      inviteMember: vi.fn().mockResolvedValue({
        error: { kind: "member_conflict", status: 409 },
        ok: false,
      }),
    });
    renderPage({ gateway });

    await selectCompany(user);
    await user.click(
      await screen.findByRole("button", { name: "Convidar membro" }),
    );
    await user.type(screen.getByLabelText("Nome"), "Carla Menezes");
    await user.type(screen.getByLabelText("E-mail"), "carla@example.test");
    await user.click(screen.getByRole("button", { name: "Convidar" }));

    expect(
      await screen.findByText("Este e-mail já possui cadastro."),
    ).toBeVisible();
    expect(screen.queryByText("Convite enviado.")).toBeNull();
  });

  it("retries an ambiguous failure with the same request id", async () => {
    const user = userEvent.setup();
    const unassignRole = vi
      .fn<PermissionsGateway["unassignRole"]>()
      .mockResolvedValueOnce({
        error: { kind: "network", mayHaveCommitted: true },
        ok: false,
      })
      .mockResolvedValueOnce({ ok: true } as PermissionsMutationResult);
    const gateway = createGateway({ unassignRole });
    renderPage({ createRequestId: () => fixedRequestId, gateway });

    await selectCompany(user);
    await removeRoleViaChip(user, "Remover papel Administrador de Ana Souza");

    expect(
      await screen.findByText(
        "A tentativa anterior terminou sem resposta confirmada. Tente novamente com os mesmos dados.",
      ),
    ).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(await screen.findByText("Papel removido.")).toBeVisible();
    expect(unassignRole).toHaveBeenCalledTimes(2);
    expect(unassignRole.mock.calls[1]?.[0]).toEqual(
      unassignRole.mock.calls[0]?.[0],
    );
  });

  it("neutralizes the view and refreshes the session after an authorization denial", async () => {
    const user = userEvent.setup();
    const getBootstrap = vi
      .fn()
      .mockResolvedValueOnce(snapshot())
      .mockResolvedValueOnce(snapshot());
    const gateway = createGateway({
      assignRole: vi.fn().mockResolvedValue({
        error: { kind: "forbidden", status: 403 },
        ok: false,
      }),
    });
    renderPage({
      gateway,
      sessionGateway: createSessionGateway(getBootstrap),
    });

    await selectCompany(user);
    await openRolesDialog(user);
    await user.click(screen.getByRole("combobox", { name: "Adicionar papel" }));
    await user.click(await screen.findByRole("option", { name: "Leitor" }));
    await user.click(screen.getByRole("button", { name: "Atribuir papel" }));

    expect(
      await screen.findByText(
        "Sua sessão não permite mais esta ação. Entre novamente.",
      ),
    ).toBeVisible();
    await waitFor(() => expect(getBootstrap).toHaveBeenCalledTimes(2));
    expect(screen.queryByText("Ana Souza")).toBeNull();
    expect(
      screen.queryByRole("button", { name: "Convidar membro" }),
    ).toBeDisabled();
  });
});
