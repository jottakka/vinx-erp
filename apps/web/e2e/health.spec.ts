import { expect, test } from "@playwright/test";

test("shows the health route", async ({ page }) => {
  await page.goto("/");

  await expect(page.getByRole("heading", { name: "Vinx ERP" })).toBeVisible();
  await expect(page.getByText("Healthy", { exact: true })).toBeVisible();
});
