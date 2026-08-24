import { expect, type Page, test } from "@playwright/test";
import { installBffSession, signIn } from "./support/bff-session";

test.beforeEach(async ({ page }) => {
  await installBffSession(page);
});

async function resolvedColor(page: Page, cssValue: string) {
  return page.evaluate((value) => {
    const probe = document.createElement("div");
    probe.style.background = value;
    document.body.appendChild(probe);
    const color = getComputedStyle(probe).backgroundColor;
    probe.remove();
    return color;
  }, cssValue);
}

async function setSelectedScheme(page: Page, label: string) {
  const appearanceTrigger = page.getByRole("button", {
    name: "Abrir preferências visuais",
  });
  await appearanceTrigger.click();
  await page.getByRole("menuitemradio", { name: label }).click();
  await page.keyboard.press("Escape");
}

async function tabUntilSelected(page: Page, selectedLabel: string) {
  for (let attempt = 0; attempt < 40; attempt += 1) {
    await page.keyboard.press("Tab");
    const active = await page.evaluate(() => {
      const element = document.activeElement;

      return element
        ? {
            ariaCurrent: element.getAttribute("aria-current"),
            label: element.textContent,
          }
        : null;
    });

    if (active?.ariaCurrent === "page" && active.label === selectedLabel) {
      return true;
    }
  }

  return false;
}

test("selected navigation pill keeps solid copper across hover and keyboard focus in both schemes", async ({
  page,
}) => {
  await page.goto("/login");
  await signIn(page);
  await expect(page).toHaveURL(/\/$/);
  await page.goto("/ajuda");
  await expect(page.getByRole("heading", { name: "Ajuda" })).toBeVisible();

  const selected = page
    .getByRole("navigation", { name: "Navegação lateral" })
    .getByRole("link", { name: "Ajuda" });
  await expect(selected).toHaveAttribute("aria-current", "page");

  for (const label of ["Tema: claro", "Tema: escuro"] as const) {
    await setSelectedScheme(page, label);

    const primaryMain = await resolvedColor(
      page,
      "var(--vinx-palette-primary-main)",
    );
    const primaryContrast = await resolvedColor(
      page,
      "var(--vinx-palette-primary-contrastText)",
    );
    expect(primaryMain, `${label}: primary main var resolves`).not.toBe(
      "rgba(0, 0, 0, 0)",
    );

    await selected.hover();
    await expect
      .poll(() =>
        selected.evaluate(
          (element) => getComputedStyle(element).backgroundColor,
        ),
      )
      .toBe(primaryMain);
    expect(
      await selected.evaluate((element) => getComputedStyle(element).color),
    ).toBe(primaryContrast);

    expect(
      await tabUntilSelected(page, "Ajuda"),
      `${label}: keyboard tab reaches the selected pill`,
    ).toBe(true);
    expect(
      await selected.evaluate((element) => element.matches(":focus-visible")),
    ).toBe(true);
    expect(
      await selected.evaluate(
        (element) => getComputedStyle(element).backgroundColor,
      ),
    ).toBe(primaryMain);
    expect(
      await selected.evaluate((element) => getComputedStyle(element).color),
    ).toBe(primaryContrast);
    expect(
      await selected.evaluate(
        (element) => getComputedStyle(element).outlineColor,
      ),
    ).toBe(primaryContrast);
  }
});
