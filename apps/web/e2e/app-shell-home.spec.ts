import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, test } from "@playwright/test";

async function enterHome(page: Page) {
  await page.goto("/login");
  await page.getByRole("button", { name: "Entrar" }).click();
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

test("Scenario: Authorized user visits preparation modules, chooses themes and restores mobile navigation focus", async ({
  page,
}) => {
  await enterHome(page);

  for (const [name, path] of [
    ["Paguai", "/paguai"],
    ["Atendimento", "/atendimento"],
    ["Kanban", "/kanban"],
    ["Pessoas", "/pessoas"],
  ] as const) {
    await page.getByRole("link", { name }).first().click();
    await expect(page).toHaveURL(new RegExp(`${path}$`));
    await expect(page.getByRole("heading", { name })).toBeVisible();
    await expect(
      page
        .getByRole("main")
        .locator("header")
        .getByText("Módulo em preparação", { exact: true }),
    ).toBeVisible();
    await page.getByRole("link", { name: "Voltar ao Início" }).click();
    await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
  }

  const appearanceTrigger = page.getByRole("button", {
    name: "Abrir preferências visuais",
  });
  await appearanceTrigger.click();
  await page.getByRole("menuitem", { name: "Tema: claro" }).click();
  await appearanceTrigger.click();
  await page.getByRole("menuitem", { name: "Tema: escuro" }).click();

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
