import { expect, type Page, test } from "@playwright/test";

async function waitForLogin(page: Page) {
  await page.goto("/login");
  await expect(
    page.getByRole("heading", {
      name: "Entrada do ambiente de desenvolvimento",
    }),
  ).toBeVisible();
}

function inkCanvas(page: Page) {
  return page.locator("canvas[aria-hidden='true']");
}

async function expectInkBehindContent(page: Page) {
  const canvas = inkCanvas(page);
  await expect(canvas).toHaveCount(1);

  const surface = await canvas.evaluate((element) => {
    const computed = getComputedStyle(element);
    const rect = element.getBoundingClientRect();

    return {
      bottom: rect.bottom,
      display: computed.display,
      height: rect.height,
      left: rect.left,
      pointerEvents: computed.pointerEvents,
      position: computed.position,
      right: rect.right,
      top: rect.top,
      width: rect.width,
      zIndex: computed.zIndex,
    };
  });

  expect(surface.position).toBe("fixed");
  expect(surface.pointerEvents).toBe("none");
  expect(surface.display).not.toBe("none");
  expect(surface.width).toBeGreaterThanOrEqual(page.viewportSize()?.width ?? 0);
  expect(Number(surface.zIndex)).toBeLessThan(1);
}

test("login renders the decorative ink field behind the content and stays usable", async ({
  page,
}) => {
  await waitForLogin(page);
  await expectInkBehindContent(page);

  await page.mouse.move(200, 200);
  await page.mouse.move(600, 420, { steps: 12 });
  await page.mouse.move(320, 260, { steps: 8 });

  const profile = page.getByLabel("Perfil de desenvolvimento");
  await expect(profile).toBeVisible();
  await profile.fill("maria");
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
});

test("the ink field reacts to the pointer without covering the login card", async ({
  page,
}) => {
  await waitForLogin(page);
  await expectInkBehindContent(page);

  const card = page.locator("main .MuiPaper-root").first();
  const cardBox = await card.boundingBox();
  await page.mouse.move(cardBox?.x ?? 0, (cardBox?.y ?? 0) + 8, { steps: 4 });
  await page.mouse.move(120, 620, { steps: 16 });

  const profile = page.getByLabel("Perfil de desenvolvimento");
  await expect(profile).toBeEditable();
  await expect(page.getByRole("button", { name: "Entrar" })).toBeEnabled();
});

test("both themes keep the ink field and recolor the surface", async ({
  page,
}) => {
  await waitForLogin(page);

  for (const optionName of ["Tema: escuro", "Tema: claro"]) {
    await page
      .getByRole("button", { name: "Abrir preferências visuais" })
      .click();
    await page.getByRole("menuitemradio", { name: optionName }).click();
    await page.keyboard.press("Escape");

    await expectInkBehindContent(page);

    const surface = await page.evaluate(() => ({
      scheme: document.documentElement.getAttribute("data-dark"),
      tone: getComputedStyle(document.body).backgroundColor,
    }));

    if (optionName === "Tema: escuro") {
      expect(surface.scheme).not.toBeNull();
    } else {
      expect(surface.scheme).toBeNull();
      expect(surface.tone).not.toBe("rgba(0, 0, 0, 0)");
    }
  }
});

test("mobile viewport keeps the ink field without horizontal overflow", async ({
  page,
}) => {
  await page.setViewportSize({ height: 800, width: 320 });
  await waitForLogin(page);
  await expectInkBehindContent(page);

  await expect(page.getByLabel("Perfil de desenvolvimento")).toBeVisible();
  await expect
    .poll(() =>
      page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    )
    .toBe(true);
});

test("reduced motion keeps a static ink field and a working login", async ({
  page,
}) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await waitForLogin(page);
  await expectInkBehindContent(page);

  await page.mouse.move(240, 300, { steps: 4 });

  const profile = page.getByLabel("Perfil de desenvolvimento");
  await profile.fill("maria");
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
});

test("forced colors hides the ink field and preserves the login", async ({
  page,
}) => {
  await page.emulateMedia({ forcedColors: "active" });
  await waitForLogin(page);

  await expect(inkCanvas(page)).toBeAttached();
  await expect(inkCanvas(page)).toHaveCSS("display", "none");

  const profile = page.getByLabel("Perfil de desenvolvimento");
  await profile.fill("maria");
  await profile.press("Tab");
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
});

test("without webgl2 the static css layers keep the login present", async ({
  page,
}) => {
  await page.addInitScript(() => {
    const original = HTMLCanvasElement.prototype.getContext;

    HTMLCanvasElement.prototype.getContext = function (
      type: string,
      options?: unknown,
    ) {
      return type === "webgl2"
        ? null
        : original.call(
            this,
            type,
            options as CanvasRenderingContext2DSettings,
          );
    };
  });

  await waitForLogin(page);

  const canvas = inkCanvas(page);
  await expect(canvas).toHaveCount(1);
  await expect(canvas).toHaveCSS("pointer-events", "none");

  const background = await canvas.evaluate(
    (element) => getComputedStyle(element).backgroundImage,
  );
  expect(background).toContain("radial-gradient");

  const profile = page.getByLabel("Perfil de desenvolvimento");
  await profile.fill("maria");
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
});
