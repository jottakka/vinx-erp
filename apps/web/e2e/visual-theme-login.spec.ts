import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, test } from "@playwright/test";

async function waitForLogin(page: Page) {
  await page.goto("/login");
  await expect(page.getByRole("heading", { name: "Entrar" })).toBeVisible();
  await expect(
    page.getByRole("combobox", { name: "Modo de cor" }),
  ).toBeEnabled();
  await expect(page.getByRole("button", { name: "Entrar" })).toBeEnabled();
}

async function enterPrototype(page: Page) {
  await waitForLogin(page);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(
    page.getByRole("heading", { name: "Protótipo autenticado" }),
  ).toBeVisible();
}

async function readSemanticSurface(page: Page) {
  return page.locator("main").evaluate((main) => {
    const root = document.documentElement;
    const computed = getComputedStyle(main);

    return {
      background: computed.backgroundColor,
      backgroundRole: getComputedStyle(root)
        .getPropertyValue("--vinx-palette-background-default")
        .trim(),
      scheme: root.hasAttribute("data-dark")
        ? "dark"
        : root.hasAttribute("data-light")
          ? "light"
          : null,
    };
  });
}

test("successful Maria entry reaches /protected with only Alfa and Beta", async ({
  page,
}) => {
  await enterPrototype(page);

  await expect(page).toHaveURL(/\/protected$/);
  await expect(page.getByText("Alfa: prototype.view")).toBeVisible();
  await expect(page.getByText("Beta: prototype.view")).toBeVisible();
  await expect(page.getByText("Gama", { exact: true })).not.toBeVisible();
});

test("invalid profile stays on /login with a visible actionable error", async ({
  page,
}) => {
  await waitForLogin(page);

  await page.getByLabel("Perfil de desenvolvimento").fill("missing-profile");
  await page.getByRole("button", { name: "Entrar" }).click();

  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole("alert")).toHaveText(
    "Perfil de desenvolvimento indisponível",
  );
});

test("anonymous /protected deep link returns to /login", async ({ page }) => {
  await page.goto("/protected");

  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole("heading", { name: "Entrar" })).toBeVisible();
});

test("reload returns to /login while the selected visual mode restores", async ({
  page,
}) => {
  await waitForLogin(page);
  await page
    .getByRole("combobox", { name: "Modo de cor" })
    .selectOption("dark");

  await expect(page.getByRole("combobox", { name: "Modo de cor" })).toHaveValue(
    "dark",
  );
  await page.reload();

  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole("combobox", { name: "Modo de cor" })).toHaveValue(
    "dark",
  );
  await expect(
    page.getByRole("heading", { name: "Protótipo autenticado" }),
  ).not.toBeVisible();
});

test("dark, light and emulated system modes expose semantic computed palette roles", async ({
  page,
}) => {
  await waitForLogin(page);

  await page
    .getByRole("combobox", { name: "Modo de cor" })
    .selectOption("light");
  const light = await readSemanticSurface(page);
  expect(light.backgroundRole).not.toBe("");
  expect(light.background).not.toBe("");

  await page
    .getByRole("combobox", { name: "Modo de cor" })
    .selectOption("dark");
  const dark = await readSemanticSurface(page);
  expect(dark.backgroundRole).not.toBe("");
  expect(dark.backgroundRole).not.toBe(light.backgroundRole);
  expect(dark.background).not.toBe(light.background);

  await page.emulateMedia({ colorScheme: "dark" });
  await page
    .getByRole("combobox", { name: "Modo de cor" })
    .selectOption("system");
  const system = await readSemanticSurface(page);
  expect(system.scheme).toBe("dark");
  expect(system.background).toBe(dark.background);
});

test("mobile login at 320 CSS pixels has no horizontal overflow", async ({
  page,
}) => {
  await page.setViewportSize({ height: 800, width: 320 });
  await waitForLogin(page);

  await expect(page.getByLabel("Perfil de desenvolvimento")).toBeVisible();
  await expect(page.getByRole("button", { name: "Entrar" })).toBeVisible();
  await expect
    .poll(() =>
      page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    )
    .toBe(true);
});

test("login and the temporary protected entry have no automated WCAG AA violations", async ({
  page,
}) => {
  await waitForLogin(page);
  await page.waitForTimeout(300);
  const loginResults = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa"])
    .analyze();
  expect(loginResults.violations).toEqual([]);

  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(
    page.getByRole("heading", { name: "Protótipo autenticado" }),
  ).toBeVisible();
  const protectedResults = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa"])
    .analyze();
  expect(protectedResults.violations).toEqual([]);
});

test("keyboard-only login and forced colors retain named controls and focus lands on submit", async ({
  page,
}) => {
  await page.emulateMedia({ forcedColors: "active" });
  await waitForLogin(page);

  const profile = page.getByLabel("Perfil de desenvolvimento");
  const enter = page.getByRole("button", { name: "Entrar" });
  await profile.focus();
  await profile.fill("maria");
  await profile.press("Tab");
  await expect(enter).toBeFocused();
  await expect(profile).toBeVisible();
  await expect(enter).toBeVisible();
  await page.keyboard.press("Enter");
  await expect(
    page.getByRole("heading", { name: "Protótipo autenticado" }),
  ).toBeVisible();
});

test("compact density changes the selected visual density and survives reload without a mock session", async ({
  page,
}) => {
  await waitForLogin(page);
  const density = page.getByRole("combobox", { name: "Densidade" });

  await density.selectOption("compact");
  await expect(density).toHaveValue("compact");
  await page.reload();

  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole("combobox", { name: "Densidade" })).toHaveValue(
    "compact",
  );
  await expect(
    page.getByRole("heading", { name: "Protótipo autenticado" }),
  ).not.toBeVisible();
});

test("explicit light mode remains unchanged when the operating-system scheme changes", async ({
  page,
}) => {
  await page.emulateMedia({ colorScheme: "dark" });
  await waitForLogin(page);
  await page
    .getByRole("combobox", { name: "Modo de cor" })
    .selectOption("light");

  const before = await readSemanticSurface(page);
  await page.emulateMedia({ colorScheme: "light" });
  const after = await readSemanticSurface(page);

  await expect(page.getByRole("combobox", { name: "Modo de cor" })).toHaveValue(
    "light",
  );
  expect(after.scheme).toBe("light");
  expect(after.background).toBe(before.background);
  expect(after.backgroundRole).toBe(before.backgroundRole);
});
