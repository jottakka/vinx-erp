import { expect, type Page, test } from "@playwright/test";
import { signIn, testCredentials } from "./support/bff-session";

const operatorUserId = "11111111-1111-4111-8111-111111111111";
const canonicalRequestIdPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

const companyId = "33333333-3333-4333-8333-333333333333";
const operatorMembershipId = "22222222-2222-4222-8222-222222222222";
const anaMembershipId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const brunoMembershipId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
const carlaMembershipId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
const adminRoleId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
const readerRoleId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
const invitationId = "ffffffff-ffff-4fff-8fff-ffffffffffff";
const roleAssignmentId = "99999999-9999-4999-8999-999999999999";

const anaMember = {
  capabilities: [],
  displayName: "Ana Souza",
  invitationStatus: null,
  membershipId: anaMembershipId,
  roles: [{ name: "Administrador", roleId: adminRoleId }],
};
const brunoMember = {
  capabilities: [],
  displayName: "Bruno Lima",
  invitationStatus: "pending_delivery",
  membershipId: brunoMembershipId,
  roles: [],
};
const carlaMember = {
  capabilities: [],
  displayName: "Carla Dias",
  invitationStatus: "pending_delivery",
  membershipId: carlaMembershipId,
  roles: [],
};
const roleCatalog = [
  {
    capabilities: [
      "foundation.roles.assign",
      "foundation.employees.create",
      "foundation.files.upload",
      "foundation.files.confirm",
    ],
    name: "Administrador",
    roleId: adminRoleId,
  },
  {
    capabilities: ["foundation.files.upload"],
    name: "Leitor",
    roleId: readerRoleId,
  },
];

type SimulatedBffOptions = {
  rolesAssignCapability?: boolean;
  invitationFirstOutcome?: "created" | "aborted";
  unassignmentStatus?: number;
};

async function installSimulatedBff(
  page: Page,
  {
    invitationFirstOutcome = "created",
    rolesAssignCapability = true,
    unassignmentStatus = 200,
  }: SimulatedBffOptions = {},
) {
  let authenticated = false;
  let invited = false;
  const snapshot = {
    companies: [
      {
        capabilities: [
          {
            name: rolesAssignCapability
              ? "foundation.roles.assign"
              : "prototype.view",
            scopes: ["Company"],
          },
        ],
        companyId,
        companyName: "Alfa",
        membershipId: operatorMembershipId,
      },
    ],
    flags: {},
    platform: { isPlatformOperator: false },
    user: { displayName: "Maria", id: operatorUserId },
  };
  const captured = {
    assignments: [] as unknown[],
    copies: [] as unknown[],
    invitations: [] as unknown[],
    membersReads: 0,
    rolesReads: 0,
    unassignments: [] as unknown[],
  };

  await page.route("**/foundation/bootstrap", async (route) => {
    await route.fulfill(
      authenticated
        ? {
            body: JSON.stringify(snapshot),
            contentType: "application/json",
            status: 200,
          }
        : { status: 401 },
    );
  });

  await page.route(
    "**/foundation/identity/browser-session/antiforgery",
    async (route) => {
      await route.fulfill({
        body: JSON.stringify({ requestToken: "test-csrf-token" }),
        contentType: "application/json",
        status: 200,
      });
    },
  );

  await page.route("**/foundation/identity/browser-session", async (route) => {
    const request = route.request();
    if (request.method() === "POST") {
      expect(request.headers()["x-csrf-token"]).toBe("test-csrf-token");
      const credentials = request.postDataJSON();
      if (
        credentials.email !== testCredentials.email ||
        credentials.password !== testCredentials.password
      ) {
        await route.fulfill({ status: 401 });
        return;
      }
      authenticated = true;
      await route.fulfill({ status: 204 });
      return;
    }

    if (request.method() === "DELETE") {
      authenticated = false;
      await route.fulfill({ status: 204 });
      return;
    }

    await route.fallback();
  });

  await page.route(
    `**/foundation/companies/${companyId}/members`,
    async (route) => {
      captured.membersReads += 1;
      await route.fulfill({
        body: JSON.stringify(
          invited
            ? [anaMember, brunoMember, carlaMember]
            : [anaMember, brunoMember],
        ),
        contentType: "application/json",
        status: 200,
      });
    },
  );

  await page.route(
    `**/foundation/companies/${companyId}/roles`,
    async (route) => {
      captured.rolesReads += 1;
      await route.fulfill({
        body: JSON.stringify(roleCatalog),
        contentType: "application/json",
        status: 200,
      });
    },
  );

  await page.route(
    `**/foundation/companies/${companyId}/role-assignments`,
    async (route) => {
      expect(route.request().method()).toBe("POST");
      expect(route.request().headers()["x-csrf-token"]).toBe("test-csrf-token");
      captured.assignments.push(route.request().postDataJSON());
      await route.fulfill({
        body: JSON.stringify({ roleAssignmentId }),
        contentType: "application/json",
        status: 200,
      });
    },
  );

  await page.route(
    `**/foundation/companies/${companyId}/role-unassignments`,
    async (route) => {
      expect(route.request().method()).toBe("POST");
      expect(route.request().headers()["x-csrf-token"]).toBe("test-csrf-token");
      captured.unassignments.push(route.request().postDataJSON());
      await route.fulfill({
        body: JSON.stringify(
          unassignmentStatus === 200
            ? { removed: true, roleAssignmentId }
            : { code: "last_admin_guard" },
        ),
        contentType: "application/json",
        status: unassignmentStatus,
      });
    },
  );

  await page.route(
    `**/foundation/companies/${companyId}/permission-copies`,
    async (route) => {
      expect(route.request().method()).toBe("POST");
      expect(route.request().headers()["x-csrf-token"]).toBe("test-csrf-token");
      captured.copies.push(route.request().postDataJSON());
      await route.fulfill({
        body: JSON.stringify({
          appliedRoleIds: [adminRoleId],
          targetMembershipId: brunoMembershipId,
        }),
        contentType: "application/json",
        status: 200,
      });
    },
  );

  await page.route(
    `**/foundation/companies/${companyId}/invitations`,
    async (route) => {
      expect(route.request().method()).toBe("POST");
      expect(route.request().headers()["x-csrf-token"]).toBe("test-csrf-token");
      captured.invitations.push(route.request().postDataJSON());
      if (
        invitationFirstOutcome === "aborted" &&
        captured.invitations.length === 1
      ) {
        await route.abort("failed");
        return;
      }
      invited = true;
      await route.fulfill({
        body: JSON.stringify({
          invitationId,
          invitationStatus: "pending_delivery",
          membershipId: carlaMembershipId,
        }),
        contentType: "application/json",
        status: 201,
      });
    },
  );

  return captured;
}

async function openPermissionsScreen(page: Page) {
  await page.goto("/permissoes");
  await expect(page).toHaveURL(/\/login$/);
  await signIn(page);
  await expect(page).toHaveURL(/\/permissoes$/);
}

async function selectAlfa(page: Page) {
  await page.getByLabel("Empresa").click();
  await page.getByRole("option", { name: "Alfa" }).click();
}

async function inviteCarla(page: Page) {
  await page.getByRole("button", { name: "Convidar membro" }).click();
  await page.getByLabel("Nome").fill("Carla Dias");
  await page.getByLabel("E-mail").fill("carla@example.test");
  await page.getByRole("button", { name: "Convidar", exact: true }).click();
}

test.describe("Permissões de membros", () => {
  test("manager invites a member, sees the receipt and the refreshed list", async ({
    page,
  }) => {
    const captured = await installSimulatedBff(page);

    await openPermissionsScreen(page);
    await selectAlfa(page);

    await expect(page.getByText("Ana Souza")).toBeVisible();
    await expect(
      page.getByRole("button", {
        name: "Remover papel Administrador de Ana Souza",
      }),
    ).toBeVisible();
    await expect(page.getByText("Convite pendente")).toBeVisible();

    await inviteCarla(page);

    await expect(
      page.getByText("Convite enviado.", { exact: true }),
    ).toBeVisible();
    await expect(page.getByText("Carla Dias")).toBeVisible();
    expect(captured.invitations).toHaveLength(1);
    expect(captured.invitations[0]).toEqual({
      displayName: "Carla Dias",
      email: "carla@example.test",
      requestId: expect.stringMatching(canonicalRequestIdPattern),
    });
  });

  test("non-manager sees an inert notice and never reads members or roles", async ({
    page,
  }) => {
    const captured = await installSimulatedBff(page, {
      rolesAssignCapability: false,
    });

    await openPermissionsScreen(page);

    await expect(
      page.getByText("Nenhuma empresa disponível para gerenciar permissões."),
    ).toBeVisible();
    await expect(page.getByLabel("Empresa")).toHaveCount(0);
    await expect(
      page.getByRole("button", { name: "Convidar membro" }),
    ).toHaveCount(0);
    expect(captured.membersReads).toBe(0);
    expect(captured.rolesReads).toBe(0);
  });

  test("last admin guard surfaces as an inline error without removing the role", async ({
    page,
  }) => {
    const captured = await installSimulatedBff(page, {
      unassignmentStatus: 409,
    });

    await openPermissionsScreen(page);
    await selectAlfa(page);

    const adminChip = page.getByRole("button", {
      name: "Remover papel Administrador de Ana Souza",
    });
    await expect(adminChip).toBeVisible();
    await adminChip.press("Backspace");

    await expect(page.getByRole("alert")).toContainText(
      "A empresa precisa de ao menos um administrador ativo.",
    );
    await expect(adminChip).toBeVisible();
    expect(captured.unassignments).toHaveLength(1);
    expect(captured.unassignments[0]).toEqual({
      membershipId: anaMembershipId,
      requestId: expect.stringMatching(canonicalRequestIdPattern),
      roleId: adminRoleId,
    });
  });

  test("retry after an ambiguous invite failure reuses the same request id", async ({
    page,
  }) => {
    const captured = await installSimulatedBff(page, {
      invitationFirstOutcome: "aborted",
    });

    await openPermissionsScreen(page);
    await selectAlfa(page);
    await inviteCarla(page);

    await expect(
      page.getByText(
        "A tentativa anterior terminou sem resposta confirmada. Tente novamente com os mesmos dados.",
      ),
    ).toBeVisible();
    await page.getByRole("button", { name: "Tentar novamente" }).click();

    await expect(
      page.getByText("Convite enviado.", { exact: true }),
    ).toBeVisible();
    await expect(page.getByText("Carla Dias")).toBeVisible();
    expect(captured.invitations).toHaveLength(2);
    const [firstAttempt, retriedAttempt] = captured.invitations as [
      { requestId?: string },
      { requestId?: string },
    ];
    expect(firstAttempt.requestId).toMatch(canonicalRequestIdPattern);
    expect(retriedAttempt.requestId).toBe(firstAttempt.requestId);
    expect(retriedAttempt).toEqual(firstAttempt);
  });

  test("assign and copy post strict commands with fresh request ids", async ({
    page,
  }) => {
    const captured = await installSimulatedBff(page);

    await openPermissionsScreen(page);
    await selectAlfa(page);

    await page.getByRole("button", { name: "Papéis de Bruno Lima" }).click();
    await page.getByRole("combobox", { name: "Adicionar papel" }).click();
    await page.getByRole("option", { name: "Leitor" }).click();
    await page.getByRole("button", { name: "Atribuir papel" }).click();

    await expect(
      page.getByText("Papel atribuído.", { exact: true }),
    ).toBeVisible();
    expect(captured.assignments).toHaveLength(1);
    expect(captured.assignments[0]).toEqual({
      membershipId: brunoMembershipId,
      requestId: expect.stringMatching(canonicalRequestIdPattern),
      roleId: readerRoleId,
    });

    await page
      .getByRole("button", { name: "Copiar de outro membro para Bruno Lima" })
      .click();
    await page.getByRole("combobox", { name: "Copiar de" }).click();
    await page.getByRole("option", { name: "Ana Souza" }).click();
    await page.getByRole("button", { name: "Copiar permissões" }).click();

    await expect(
      page.getByText("Permissões copiadas.", { exact: true }),
    ).toBeVisible();
    expect(captured.copies).toHaveLength(1);
    expect(captured.copies[0]).toEqual({
      requestId: expect.stringMatching(canonicalRequestIdPattern),
      sourceMembershipId: anaMembershipId,
      targetMembershipId: brunoMembershipId,
    });
  });
});
