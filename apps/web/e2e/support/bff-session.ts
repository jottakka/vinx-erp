import { expect, type Page } from "@playwright/test";

export const testCredentials = {
  email: "maria@example.test",
  password: "test-password",
} as const;

const snapshot = {
  companies: [
    {
      capabilities: [{ name: "prototype.view", scopes: ["Company"] }],
      companyId: "33333333-3333-4333-8333-333333333333",
      companyName: "Alfa",
      membershipId: "22222222-2222-4222-8222-222222222222",
    },
    {
      capabilities: [{ name: "prototype.view", scopes: ["Company"] }],
      companyId: "55555555-5555-4555-8555-555555555555",
      companyName: "Beta",
      membershipId: "44444444-4444-4444-8444-444444444444",
    },
  ],
  flags: {},
  platform: { isPlatformOperator: false },
  user: {
    displayName: "Maria",
    id: "11111111-1111-4111-8111-111111111111",
  },
} as const;

export async function installBffSession(page: Page) {
  let authenticated = false;

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
      expect(request.headers()["x-csrf-token"]).toBe("test-csrf-token");
      authenticated = false;
      await route.fulfill({ status: 204 });
      return;
    }

    await route.fallback();
  });
}

export async function signIn(page: Page) {
  await page.getByLabel("E-mail").fill(testCredentials.email);
  await page.getByLabel("Senha").fill(testCredentials.password);
  await page.getByRole("button", { name: "Entrar" }).click();
}
