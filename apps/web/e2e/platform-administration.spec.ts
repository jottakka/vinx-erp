import { expect, type Page, test } from "@playwright/test";
import { testCredentials } from "./support/bff-session";

const operatorUserId = "11111111-1111-4111-8111-111111111111";
const canonicalRequestIdPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

function snapshot(isPlatformOperator: boolean) {
  return {
    companies: [],
    flags: {},
    platform: { isPlatformOperator },
    user: { displayName: "Operador", id: operatorUserId },
  };
}

const provisionReceipt = {
  groupId: "21111111-2222-3333-4444-555555555555",
  companyId: "22222222-3333-4444-5555-666666666666",
  ownerUserId: "23333333-4444-5555-6666-777777777777",
  ownerMembershipId: "24444444-5555-6666-7777-888888888888",
  ownerInvitationId: "25555555-6666-7777-8888-999999999999",
  invitationStatus: "pending_delivery",
};

type SimulatedBffOptions = {
  isPlatformOperator?: boolean;
  provisionStatus?: number;
  provisionBody?: Record<string, unknown>;
};

async function installSimulatedBff(
  page: Page,
  {
    isPlatformOperator = true,
    provisionBody = provisionReceipt,
    provisionStatus = 201,
  }: SimulatedBffOptions = {},
) {
  let authenticated = false;
  const provisionRequests: unknown[] = [];

  await page.route("**/foundation/bootstrap", async (route) => {
    await route.fulfill(
      authenticated
        ? {
            body: JSON.stringify(snapshot(isPlatformOperator)),
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

  await page.route("**/foundation/platform/organizations", async (route) => {
    const request = route.request();
    expect(request.method()).toBe("POST");
    expect(request.headers()["x-csrf-token"]).toBe("test-csrf-token");
    provisionRequests.push(request.postDataJSON());
    await route.fulfill({
      body: JSON.stringify(provisionBody),
      contentType: "application/json",
      headers: { "X-Correlation-ID": "12345678-1234-1234-1234-123456789abc" },
      status: provisionStatus,
    });
  });

  return { provisionRequests };
}

async function signInAsOperator(page: Page) {
  await page.goto("/administracao");
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel("E-mail").fill(testCredentials.email);
  await page.getByLabel("Senha").fill(testCredentials.password);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page).toHaveURL(/\/administracao$/);
}

async function fillProvisioningForm(page: Page) {
  await page.getByLabel("Nome do grupo").fill("Grupo Northwind");
  await page.getByLabel("Nome da empresa").fill("Northwind HQ");
  await page.getByLabel("Nome do dono").fill("Owner");
  await page.getByLabel("E-mail do dono").fill("owner@example.test");
}

test("operator provisions the first organization with a pending delivery receipt", async ({
  page,
}) => {
  const { provisionRequests } = await installSimulatedBff(page);

  await signInAsOperator(page);
  await fillProvisioningForm(page);
  await page.getByRole("button", { name: "Criar organização" }).click();

  await expect(
    page.getByText("Organização criada. Convite pendente de entrega."),
  ).toBeVisible();
  expect(provisionRequests).toHaveLength(1);
  expect(provisionRequests[0]).toEqual({
    companyName: "Northwind HQ",
    groupName: "Grupo Northwind",
    ownerDisplayName: "Owner",
    ownerEmail: "owner@example.test",
    requestId: expect.stringMatching(canonicalRequestIdPattern),
  });
  expect(page.url()).not.toContain("token");
});

test("non-operator never sees the administrative action", async ({ page }) => {
  const { provisionRequests } = await installSimulatedBff(page, {
    isPlatformOperator: false,
  });

  await signInAsOperator(page);

  await expect(
    page.getByText(
      "Esta área está disponível apenas para operadores de plataforma.",
    ),
  ).toBeVisible();
  await expect(page.getByLabel("Nome do grupo")).toHaveCount(0);
  expect(provisionRequests).toHaveLength(0);
});

test("step-up denial blocks further submissions without hidden retries", async ({
  page,
}) => {
  const { provisionRequests } = await installSimulatedBff(page, {
    provisionBody: { code: "mfa_assurance_required" },
    provisionStatus: 403,
  });

  await signInAsOperator(page);
  await fillProvisioningForm(page);
  await page.getByRole("button", { name: "Criar organização" }).click();

  await expect(
    page.getByText(
      "Esta ação exige autenticação com segundo fator. Saia e entre novamente com MFA para continuar.",
    ),
  ).toBeVisible();
  const submitButton = page.getByRole("button", { name: "Criar organização" });
  await expect(submitButton).toBeDisabled();
  await expect(
    page.getByRole("alert").getByRole("button", { name: "Sair" }),
  ).toBeVisible();
  await page.waitForTimeout(750);
  expect(provisionRequests).toHaveLength(1);
  await expect(
    page.getByText("Organização criada. Convite pendente de entrega."),
  ).toHaveCount(0);
});
