import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, test } from "@playwright/test";

async function waitForLogin(page: Page) {
  await page.goto("/login");
  await expect(
    page.getByRole("heading", {
      name: "Entrada do ambiente de desenvolvimento",
    }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Abrir preferências visuais" }),
  ).toBeEnabled();
  await expect(page.getByRole("button", { name: "Entrar" })).toBeEnabled();
}

async function chooseAppearance(page: Page, optionName: string) {
  await page
    .getByRole("button", { name: "Abrir preferências visuais" })
    .click();
  await page.getByRole("menuitemradio", { name: optionName }).click();
}

async function expectSelectedAppearance(page: Page, optionName: string) {
  await page
    .getByRole("button", { name: "Abrir preferências visuais" })
    .click();
  await expect(
    page.getByRole("menuitemradio", { name: optionName }),
  ).toHaveAttribute("aria-checked", "true");
  await page.keyboard.press("Escape");
}

async function enterApplication(page: Page) {
  await waitForLogin(page);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
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

test("Scenario: Maria uses the professional development entry", async ({
  page,
}) => {
  await page.setViewportSize({ height: 800, width: 320 });
  await page.goto("/login");

  await chooseAppearance(page, "Tema: escuro");
  await chooseAppearance(page, "Densidade: compacta");
  await expectSelectedAppearance(page, "Densidade: compacta");

  const loginCard = page.locator("main .MuiPaper-root");
  await expect(loginCard).toHaveCSS("padding-top", "12px");
  await expect(loginCard.locator(":scope > .MuiStack-root")).toHaveCSS(
    "gap",
    "16px",
  );

  await page.getByLabel("Perfil de desenvolvimento").fill("maria");
  await page.getByRole("button", { name: "Entrar" }).click();

  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole("heading", { name: "Início" })).toHaveCount(1);
  await expect
    .poll(() =>
      page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    )
    .toBe(true);
});

test("Scenario: Invalid development profile remains recoverable", async ({
  page,
}) => {
  await page.goto("/login");
  const profile = page.getByLabel("Perfil de desenvolvimento");

  for (const value of ["", "   ", "unavailable-profile"]) {
    await profile.fill(value);
    await page.getByRole("button", { name: "Entrar" }).click();
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole("alert")).toBeVisible();
    await expect(profile).toBeFocused();
  }
});

test("successful Maria entry reaches all-authorized home with only Alfa and Beta", async ({
  page,
}) => {
  await enterApplication(page);

  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole("group", { name: "Empresa Alfa" })).toBeVisible();
  await expect(page.getByRole("group", { name: "Empresa Beta" })).toBeVisible();
  await expect(page.getByText("Gama", { exact: true })).not.toBeVisible();
  await expect(page.getByText("prototype.view")).not.toBeVisible();
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
  await expect(
    page.getByRole("heading", {
      name: "Entrada do ambiente de desenvolvimento",
    }),
  ).toBeVisible();
});

test("reload returns to /login while the selected visual mode restores", async ({
  page,
}) => {
  await waitForLogin(page);
  await chooseAppearance(page, "Tema: escuro");

  await expectSelectedAppearance(page, "Tema: escuro");
  await page.reload();

  await expect(page).toHaveURL(/\/login$/);
  await expectSelectedAppearance(page, "Tema: escuro");
  await expect(page.getByRole("heading", { name: "Início" })).not.toBeVisible();
});

test("appearance menu exposes only light and dark themes", async ({ page }) => {
  await waitForLogin(page);

  await page
    .getByRole("button", { name: "Abrir preferências visuais" })
    .click();
  await expect(
    page.getByRole("menuitemradio", { name: "Tema: claro" }),
  ).toBeVisible();
  await expect(
    page.getByRole("menuitemradio", { name: "Tema: escuro" }),
  ).toBeVisible();
  await expect(page.getByRole("menuitemradio", { name: /^Tema:/ })).toHaveCount(
    2,
  );
});

test("Help describes only the light and dark themes", async ({ page }) => {
  await enterApplication(page);
  await page.getByRole("link", { name: "Abrir ajuda" }).click();

  await expect(page.getByRole("heading", { name: "Ajuda" })).toBeVisible();
  await expect(page.getByText(/tema claro ou escuro/i)).toBeVisible();
  await expect(page.getByText(/sistema/i)).toHaveCount(0);
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

test("login and the authenticated Home have no automated WCAG AA violations", async ({
  page,
}) => {
  await waitForLogin(page);
  await page.waitForTimeout(300);
  const loginResults = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa"])
    .analyze();
  expect(loginResults.violations).toEqual([]);

  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
  await page
    .getByRole("button", { name: "Abrir preferências visuais" })
    .click();
  await expect(page.getByRole("menu").locator("..")).toHaveCSS("opacity", "1");
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
  await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
});

test("compact density changes the selected visual density and survives reload without a mock session", async ({
  page,
}) => {
  await waitForLogin(page);
  await chooseAppearance(page, "Densidade: compacta");
  await expectSelectedAppearance(page, "Densidade: compacta");
  await page.reload();

  await expect(page).toHaveURL(/\/login$/);
  await expectSelectedAppearance(page, "Densidade: compacta");
  await expect(page.getByRole("heading", { name: "Início" })).not.toBeVisible();
});

test("explicit light mode remains unchanged when the operating-system scheme changes", async ({
  page,
}) => {
  await page.emulateMedia({ colorScheme: "dark" });
  await waitForLogin(page);
  await chooseAppearance(page, "Tema: claro");

  const before = await readSemanticSurface(page);
  await page.emulateMedia({ colorScheme: "light" });
  const after = await readSemanticSurface(page);

  await expectSelectedAppearance(page, "Tema: claro");
  expect(after.scheme).toBe("light");
  expect(after.background).toBe(before.background);
  expect(after.backgroundRole).toBe(before.backgroundRole);
});

test("keyboard closes the appearance menu and restores focus to its trigger", async ({
  page,
}) => {
  await waitForLogin(page);
  const trigger = page.getByRole("button", {
    name: "Abrir preferências visuais",
  });

  await trigger.focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("menu")).toBeVisible();
  await page.keyboard.press("Escape");

  await expect(page.getByRole("menu")).not.toBeVisible();
  await expect(trigger).toBeFocused();
});
