import { expect, type Page } from "@playwright/test";
import { signIn, testCredentials } from "./bff-session";

export const operatorUserId = "11111111-1111-4111-8111-111111111111";
export const alfaCompanyId = "33333333-3333-4333-8333-333333333333";
export const alfaMembershipId = "22222222-2222-4222-8222-222222222222";
export const betaCompanyId = "55555555-5555-4555-8555-555555555555";
export const betaMembershipId = "44444444-4444-4444-8444-444444444444";

export const csrfToken = "test-csrf-token";
export const canonicalRequestIdPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export type ModuleCapability = {
  name: string;
  scopes?: readonly string[];
};

export type ModuleCompanyMembership = {
  capabilities: readonly ModuleCapability[];
  companyId: string;
  companyName: string;
  membershipId: string;
};

/**
 * Sessão simulada para as páginas de módulo (Paguai, Atendimento, Demandas):
 * bootstrap com N empresas autorizadas, antiforgery, login/logout e contador
 * de notificações do shell. As rotas de cada módulo ficam nas specs.
 */
export async function installModuleSession(
  page: Page,
  companies: readonly ModuleCompanyMembership[],
) {
  let authenticated = false;
  const snapshot = {
    companies: companies.map((company) => ({
      capabilities: company.capabilities.map((capability) => ({
        name: capability.name,
        scopes: [...(capability.scopes ?? ["Company"])],
      })),
      companyId: company.companyId,
      companyName: company.companyName,
      membershipId: company.membershipId,
    })),
    flags: {},
    platform: { isPlatformOperator: false },
    user: { displayName: "Maria", id: operatorUserId },
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
        body: JSON.stringify({ requestToken: csrfToken }),
        contentType: "application/json",
        status: 200,
      });
    },
  );

  await page.route("**/foundation/identity/browser-session", async (route) => {
    const request = route.request();
    if (request.method() === "POST") {
      expect(request.headers()["x-csrf-token"]).toBe(csrfToken);
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

  await page.route("**/notifications/inbox/unread-count*", async (route) => {
    await route.fulfill({
      body: JSON.stringify({ unreadCount: 0 }),
      contentType: "application/json",
      status: 200,
    });
  });
}

/** Deep link autenticado: rota protegida redireciona ao login e volta. */
export async function openModulePage(page: Page, path: string) {
  await page.goto(path);
  await expect(page).toHaveURL(/\/login$/);
  await signIn(page);
  await expect(page).toHaveURL(new RegExp(`${path.replace(/\?.*/, "")}$`));
}
