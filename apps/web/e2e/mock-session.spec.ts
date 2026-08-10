import { expect, type Page, test } from "@playwright/test";

async function enterPrototype(page: Page) {
  await page.goto("/login");
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(
    page.getByRole("heading", { name: "Protótipo autenticado" }),
  ).toBeVisible();
}

test("enters the prototype with only authorized mock companies", async ({
  page,
}) => {
  await enterPrototype(page);

  await expect(page.getByText("Alfa: prototype.view")).toBeVisible();
  await expect(page.getByText("Beta: prototype.view")).toBeVisible();
  await expect(page.getByText("Gama", { exact: true })).not.toBeVisible();
});

test("redirects an anonymous protected deep link to login", async ({
  page,
}) => {
  await page.goto("/protected");

  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole("heading", { name: "Entrar" })).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Protótipo autenticado" }),
  ).not.toBeVisible();
});

test("logout removes access to the protected prototype", async ({ page }) => {
  await enterPrototype(page);

  await page.getByRole("button", { name: "Sair do protótipo" }).click();

  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole("heading", { name: "Entrar" })).toBeVisible();
});

test("reload returns to login because the mock session is in memory only", async ({
  page,
}) => {
  await enterPrototype(page);

  await page.reload();

  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole("heading", { name: "Entrar" })).toBeVisible();
});
