import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, test } from "@playwright/test";
import { installBffSession, signIn } from "./support/bff-session";

test.beforeEach(async ({ page }) => {
  await installBffSession(page);
});

async function enterHome(page: Page) {
  await page.goto("/login");
  await signIn(page);
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
}

test("Scenario: Mobile drawer opens, closes predictably and restores focus without duplicate navigation", async ({
  page,
}) => {
  await page.setViewportSize({ height: 800, width: 320 });
  await enterHome(page);

  const trigger = page.getByRole("button", { name: "Abrir navegação" });
  await trigger.focus();
  await trigger.press("Enter");
  const drawerNavigation = page.getByRole("navigation", {
    name: "Navegação principal",
  });
  await expect(drawerNavigation).toBeVisible();

  await page.keyboard.press("Escape");
  await expect(drawerNavigation).not.toBeVisible();
  await expect(trigger).toBeFocused();
  await expect(trigger).toHaveAttribute("aria-expanded", "false");
  await expect(trigger).not.toHaveAttribute("aria-controls");

  await trigger.click();
  await expect(drawerNavigation).toBeVisible();
  await page.locator(".MuiBackdrop-root").click({
    position: { x: 315, y: 400 },
  });
  await expect(drawerNavigation).not.toBeVisible();

  await trigger.click();
  await expect(drawerNavigation).toBeVisible();
  await drawerNavigation.getByRole("link", { name: "Ajuda" }).click();

  await expect(page).toHaveURL(/\/ajuda$/);
  await expect(page.getByRole("heading", { name: "Ajuda" })).toHaveCount(1);
  await expect(trigger).toHaveAttribute("aria-expanded", "false");
  await expect(trigger).not.toHaveAttribute("aria-controls");
  await expect
    .poll(() =>
      page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    )
    .toBe(true);
});

test("authenticated shell keeps accessible landmarks and semantic colors across forced colors", async ({
  page,
}) => {
  await page.emulateMedia({ forcedColors: "active" });
  await enterHome(page);

  await expect(page.getByRole("main")).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Pular para o conteúdo principal" }),
  ).toBeAttached();
  await expect(
    page.getByRole("button", { name: "Abrir preferências visuais" }),
  ).toBeVisible();
  await expect(page.getByRole("link", { name: "Abrir ajuda" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Sair" })).toBeVisible();

  const results = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa"])
    .analyze();
  expect(results.violations).toEqual([]);
});

test("Scenario: Authorized user visits modules, chooses themes and restores mobile navigation focus", async ({
  page,
}) => {
  await enterHome(page);
  const homeModuleSection = page
    .getByRole("heading", { name: "Áreas disponíveis" })
    .locator("xpath=ancestor::section[1]");
  const sidebar = page.getByRole("navigation", { name: "Navegação lateral" });

  for (const [name, path] of [
    ["Paguai", "/paguai"],
    ["Atendimento", "/atendimento"],
    ["Pessoas", "/pessoas"],
  ] as const) {
    await homeModuleSection.getByRole("link", { name }).click();
    await expect(page).toHaveURL(new RegExp(`${path}$`));
    // exact: evita colisão de substring com "Novo atendimento" (h6 da página).
    await expect(
      page.getByRole("heading", { name, exact: true }),
    ).toBeVisible();
    await sidebar.getByRole("link", { name: "Início" }).click();
    await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
  }

  const appearanceTrigger = page.getByRole("button", {
    name: "Abrir preferências visuais",
  });
  await appearanceTrigger.click();
  await page.getByRole("menuitemradio", { name: "Tema: claro" }).click();
  await appearanceTrigger.click();
  await expect(
    page.getByRole("menuitemradio", { name: "Tema: claro" }),
  ).toHaveAttribute("aria-checked", "true");
  await page.getByRole("menuitemradio", { name: "Tema: escuro" }).click();
  await appearanceTrigger.click();
  await expect(
    page.getByRole("menuitemradio", { name: "Tema: escuro" }),
  ).toHaveAttribute("aria-checked", "true");
  await page.keyboard.press("Escape");

  await page.setViewportSize({ height: 800, width: 320 });
  const navigationTrigger = page.getByRole("button", {
    name: "Abrir navegação",
  });
  await navigationTrigger.focus();
  await navigationTrigger.press("Enter");
  await expect(
    page.getByRole("navigation", { name: "Navegação principal" }),
  ).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(navigationTrigger).toBeFocused();
});

test("production preview hides demonstration navigation and Home prototype actions", async ({
  page,
}) => {
  await enterHome(page);

  await expect(
    page
      .getByRole("navigation", { name: "Navegação lateral" })
      .getByText("Demonstração", { exact: true }),
  ).not.toBeVisible();
  await expect(
    page.getByRole("link", { name: "Abrir protótipo Paguai" }),
  ).not.toBeVisible();
});

for (const path of ["/prototipos", "/prototipos/paguai"]) {
  test(`production preview resolves ${path} through authenticated wildcard to Home`, async ({
    page,
  }) => {
    await page.goto(path);
    await expect(page).toHaveURL(/\/login$/);
    await signIn(page);

    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
  });
}
