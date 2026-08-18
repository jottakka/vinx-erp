import { expect, type Page, test } from "@playwright/test";
import { installBffSession, signIn } from "./support/bff-session";

test.beforeEach(async ({ page }) => {
  await installBffSession(page);
});

async function enterApplication(page: Page) {
  await page.goto("/login");
  await signIn(page);
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
}

test("Scenario: Maria signs in and reaches a professional all-authorized home", async ({
  page,
}) => {
  await enterApplication(page);

  await expect(
    page.getByText("Todas as empresas autorizadas", { exact: true }),
  ).toBeVisible();
  await expect(page.getByRole("group", { name: "Empresa Alfa" })).toBeVisible();
  await expect(page.getByRole("group", { name: "Empresa Beta" })).toBeVisible();
  await expect(page.getByText("Gama", { exact: true })).not.toBeVisible();
  await expect(page.getByText("prototype.view")).not.toBeVisible();
});

test("Scenario: Anonymous deep link returns safely to login", async ({
  page,
}) => {
  await page.goto("/ajuda");

  await expect(page).toHaveURL(/\/login$/);
  await signIn(page);

  await expect(page).toHaveURL(/\/ajuda$/);
  await expect(page.getByRole("heading", { name: "Ajuda" })).toBeVisible();
});

test("Scenario: Unknown or query-bearing return path is rejected", async ({
  page,
}) => {
  for (const path of ["/caminho-desconhecido", "/ajuda?origem=externa"]) {
    await installBffSession(page);
    await page.goto(path);

    await expect(page).toHaveURL(/\/login$/);
    await signIn(page);

    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
  }
});

test("successful logout removes access to the authenticated application", async ({
  page,
}) => {
  await enterApplication(page);

  await page.getByRole("button", { name: "Sair" }).click();

  await expect(page).toHaveURL(/\/login$/);
  await expect(
    page.getByRole("heading", {
      name: "Entrar",
    }),
  ).toBeVisible();
});

test("reload restores the authenticated session through the BFF contract", async ({
  page,
}) => {
  await enterApplication(page);
  await page.reload();

  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
});
