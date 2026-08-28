import { expect, test } from "@playwright/test";
import { installBffSession } from "./support/bff-session";

test.beforeEach(async ({ page }) => {
  await installBffSession(page);
});

test("shows the public login fallback", async ({ page }) => {
  await page.goto("/");

  await expect(page).toHaveURL(/\/login$/);
  await expect(
    page.getByRole("heading", {
      name: "Entrar",
    }),
  ).toBeVisible();
});
