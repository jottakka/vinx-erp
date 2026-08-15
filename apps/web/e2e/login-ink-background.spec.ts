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
  return page.locator("canvas[data-ink-active='true']");
}

function inkFallback(page: Page) {
  return page.locator("[data-ink-fallback]");
}

type CanvasFrame = {
  data: number[];
  height: number;
  width: number;
};

async function readInkFrame(page: Page): Promise<CanvasFrame> {
  return inkCanvas(page).evaluate((element) => {
    const canvas = element as HTMLCanvasElement;
    const context = canvas.getContext("2d");

    if (!context) {
      throw new Error("Expected the Canvas2D ink renderer");
    }

    return {
      data: Array.from(
        context.getImageData(0, 0, canvas.width, canvas.height).data,
      ),
      height: canvas.height,
      width: canvas.width,
    };
  });
}

function meanFrameDifference(
  before: CanvasFrame,
  after: CanvasFrame,
  includes: (x: number, y: number) => boolean,
) {
  let difference = 0;
  let samples = 0;

  for (let y = 0; y < before.height; y += 1) {
    for (let x = 0; x < before.width; x += 1) {
      if (!includes(x, y)) {
        continue;
      }

      const offset = (y * before.width + x) * 4;
      difference +=
        Math.abs(before.data[offset] - after.data[offset]) +
        Math.abs(before.data[offset + 1] - after.data[offset + 1]) +
        Math.abs(before.data[offset + 2] - after.data[offset + 2]) +
        Math.abs(before.data[offset + 3] - after.data[offset + 3]);
      samples += 4;
    }
  }

  return difference / Math.max(samples, 1);
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

  const profile = page.getByLabel("Perfil de desenvolvimento");
  await expect(profile).toBeVisible();
  await profile.fill("maria");
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
});

test("the ambient ink field does not cover the login card", async ({
  page,
}) => {
  await waitForLogin(page);
  await expectInkBehindContent(page);

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
  await expect(inkFallback(page)).toHaveCSS("display", "none");

  const profile = page.getByLabel("Perfil de desenvolvimento");
  await profile.fill("maria");
  await profile.press("Tab");
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
});

test("without webgl2 Canvas2D renders ambient smoke and ignores the pointer", async ({
  page,
}) => {
  await page.addInitScript(() => {
    const original = HTMLCanvasElement.prototype.getContext;
    const originalAddEventListener = EventTarget.prototype.addEventListener;
    const state = window as typeof window & {
      __inkPointerListeners: Array<{ passive: boolean; type: string }>;
    };
    state.__inkPointerListeners = [];

    EventTarget.prototype.addEventListener = function (
      type: string,
      listener: EventListenerOrEventListenerObject | null,
      options?: AddEventListenerOptions | boolean,
    ) {
      if (
        this === window &&
        (type === "pointermove" || type === "pointerdown")
      ) {
        state.__inkPointerListeners.push({
          passive: typeof options === "object" && options?.passive === true,
          type,
        });
      }

      originalAddEventListener.call(this, type, listener, options);
    };

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
  await page.setViewportSize({ height: 800, width: 1000 });

  await waitForLogin(page);

  const canvas = inkCanvas(page);
  const fallback = inkFallback(page);
  await expect(canvas).toHaveCount(1);
  await expect(fallback).toHaveCount(1);
  expect(await fallback.evaluate((element) => element.tagName)).toBe("DIV");
  await expect(canvas).toHaveCSS("pointer-events", "none");
  await expect(fallback).toHaveCSS("pointer-events", "none");
  await expect(canvas).toHaveCSS("background-image", "none");
  await expect(fallback).toHaveCSS("display", "none");
  await expect
    .poll(() => canvas.evaluate((element) => element.width))
    .toBeGreaterThan(0);

  const firstFrame = await readInkFrame(page);
  const alphaBuckets = new Set(
    Array.from({ length: firstFrame.width * firstFrame.height }, (_, index) =>
      Math.round(firstFrame.data[index * 4 + 3] / 8),
    ),
  );
  const activeQuadrants = new Set<number>();

  for (let y = 0; y < firstFrame.height; y += 1) {
    for (let x = 0; x < firstFrame.width; x += 1) {
      if (firstFrame.data[(y * firstFrame.width + x) * 4 + 3] > 20) {
        activeQuadrants.add(
          (x >= firstFrame.width / 2 ? 1 : 0) +
            (y >= firstFrame.height / 2 ? 2 : 0),
        );
      }
    }
  }

  expect(firstFrame.width).toBeLessThanOrEqual(240);
  expect(firstFrame.height).toBeLessThanOrEqual(160);
  expect(alphaBuckets.size).toBeGreaterThan(12);
  expect(activeQuadrants.size).toBeGreaterThanOrEqual(3);

  await page.waitForTimeout(96);
  const evolvedFrame = await readInkFrame(page);
  const temporalDifference = meanFrameDifference(
    firstFrame,
    evolvedFrame,
    () => true,
  );
  expect(temporalDifference).toBeGreaterThan(0.3);
  expect(
    await page.evaluate(() =>
      (
        window as typeof window & {
          __inkPointerListeners: Array<{ passive: boolean; type: string }>;
        }
      ).__inkPointerListeners.filter(({ passive }) => passive),
    ),
  ).toEqual([]);

  const pointer = { x: 720, y: 650 };
  await page.evaluate(() => {
    Object.defineProperty(document, "hidden", {
      configurable: true,
      value: true,
    });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  const beforePointer = await readInkFrame(page);
  await page.mouse.move(pointer.x - 120, pointer.y);
  await page.mouse.move(pointer.x, pointer.y, { steps: 8 });
  await page.mouse.down();
  await page.waitForTimeout(48);
  await page.mouse.up();
  const afterPointer = await readInkFrame(page);
  expect(meanFrameDifference(beforePointer, afterPointer, () => true)).toBe(0);

  await page.evaluate(() => {
    Object.defineProperty(document, "hidden", {
      configurable: true,
      value: false,
    });
    document.dispatchEvent(new Event("visibilitychange"));
  });

  const profile = page.getByLabel("Perfil de desenvolvimento");
  await profile.fill("maria");
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
});
