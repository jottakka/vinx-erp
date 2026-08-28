import { useColorScheme } from "@mui/material/styles";
import { render, screen, waitFor } from "@testing-library/react";
import { useEffect } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { InkBackground } from "./ink-background";
import { createInkFallbackLayers } from "./theme/ink-field-style";
import { VinxThemeProvider } from "./theme/vinx-theme-provider";

type StubGl = Record<string, unknown> & {
  drawArrays: ReturnType<typeof vi.fn>;
  uniform3f: ReturnType<typeof vi.fn>;
  uniform4fv: ReturnType<typeof vi.fn>;
  deleteProgram: ReturnType<typeof vi.fn>;
  getExtension: ReturnType<typeof vi.fn>;
};

function createStubGl() {
  return {
    VERTEX_SHADER: 35633,
    FRAGMENT_SHADER: 35632,
    COMPILE_STATUS: 35713,
    LINK_STATUS: 35714,
    TRIANGLES: 4,
    canvas: null as HTMLCanvasElement | null,
    createShader: vi.fn(() => ({})),
    shaderSource: vi.fn(),
    compileShader: vi.fn(),
    getShaderParameter: vi.fn(() => true),
    createProgram: vi.fn(() => ({})),
    attachShader: vi.fn(),
    linkProgram: vi.fn(),
    deleteShader: vi.fn(),
    getProgramParameter: vi.fn(() => true),
    deleteProgram: vi.fn(),
    getUniformLocation: vi.fn(() => ({})),
    getExtension: vi.fn(() => ({ loseContext: vi.fn() })),
    useProgram: vi.fn(),
    viewport: vi.fn(),
    uniform2f: vi.fn(),
    uniform1f: vi.fn(),
    uniform1i: vi.fn(),
    uniform3f: vi.fn(),
    uniform4fv: vi.fn(),
    drawArrays: vi.fn(),
  };
}

function stubMatchMedia(matchesFor: (query: string) => boolean) {
  vi.stubGlobal(
    "matchMedia",
    (query: string) =>
      ({
        matches: matchesFor(query),
        media: query,
        onchange: null,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        addListener: vi.fn(),
        removeListener: vi.fn(),
        dispatchEvent: vi.fn(),
      }) as unknown as MediaQueryList,
  );
}

function stubWebglCanvas(gl: StubGl) {
  const originalCreateElement = document.createElement.bind(document);

  vi.spyOn(document, "createElement").mockImplementation(
    (tagName: string, options?: ElementCreationOptions) => {
      const element = originalCreateElement(tagName, options);

      if (tagName.toLowerCase() === "canvas") {
        const canvas = element as HTMLCanvasElement;
        vi.spyOn(canvas, "getContext").mockImplementation((type: string) => {
          if (type !== "webgl2") {
            return null;
          }

          gl.canvas = canvas;
          return gl as unknown as WebGL2RenderingContext;
        });
      }

      return element;
    },
  );
}

function stubCanvas2dFallback() {
  const originalCreateElement = document.createElement.bind(document);
  const context = {
    canvas: null as HTMLCanvasElement | null,
    clearRect: vi.fn(),
    createImageData: vi.fn((width: number, height: number) => ({
      data: new Uint8ClampedArray(width * height * 4),
      height,
      width,
    })),
    putImageData: vi.fn(),
  };

  vi.spyOn(document, "createElement").mockImplementation(
    (tagName: string, options?: ElementCreationOptions) => {
      const element = originalCreateElement(tagName, options);

      if (tagName.toLowerCase() === "canvas") {
        const canvas = element as HTMLCanvasElement;
        context.canvas = canvas;
        vi.spyOn(canvas, "getContext").mockImplementation((type: string) =>
          type === "2d"
            ? (context as unknown as CanvasRenderingContext2D)
            : null,
        );
      }

      return element;
    },
  );

  return context;
}

function stubLockedWebglCanvas() {
  const originalCreateElement = document.createElement.bind(document);
  const gl = createStubGl() as unknown as StubGl;
  gl.getShaderParameter = vi.fn(() => false);
  const context = {
    canvas: null as HTMLCanvasElement | null,
    clearRect: vi.fn(),
    createImageData: vi.fn((width: number, height: number) => ({
      data: new Uint8ClampedArray(width * height * 4),
      height,
      width,
    })),
    putImageData: vi.fn(),
  };
  let canvasIndex = 0;

  vi.spyOn(document, "createElement").mockImplementation(
    (tagName: string, options?: ElementCreationOptions) => {
      const element = originalCreateElement(tagName, options);

      if (tagName.toLowerCase() !== "canvas") {
        return element;
      }

      const canvas = element as HTMLCanvasElement;
      const currentIndex = canvasIndex;
      canvasIndex += 1;
      vi.spyOn(canvas, "getContext").mockImplementation((type: string) => {
        if (currentIndex === 0) {
          if (type === "webgl2") {
            gl.canvas = canvas;
            return gl as unknown as WebGL2RenderingContext;
          }

          return null;
        }

        if (type === "2d") {
          context.canvas = canvas;
          return context as unknown as CanvasRenderingContext2D;
        }

        return null;
      });

      return canvas;
    },
  );

  return { context, gl };
}

function runtimeCssFor(element: Element) {
  const selectors = Array.from(element.classList, (name) => `.${name}`);

  return Array.from(document.styleSheets)
    .flatMap((styleSheet) => Array.from(styleSheet.cssRules))
    .map((rule) => rule.cssText)
    .filter((rule) => selectors.some((selector) => rule.includes(selector)))
    .join("\n");
}

function ModeProbe({ mode }: { mode: "dark" | "light" }) {
  const { setMode } = useColorScheme();

  useEffect(() => {
    setMode(mode);
  }, [mode, setMode]);

  return null;
}

function mountBackground(mode?: "dark" | "light") {
  return render(
    <VinxThemeProvider>
      {mode ? <ModeProbe mode={mode} /> : null}
      <InkBackground />
    </VinxThemeProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("InkBackground", () => {
  it("renders a decorative canvas that never intercepts interaction", async () => {
    mountBackground();

    const canvas = await screen.findAllByRole("generic");

    expect(canvas.length).toBeGreaterThan(0);

    const inkCanvas = document.querySelector<HTMLCanvasElement>(
      "canvas[aria-hidden='true']",
    );

    expect(inkCanvas).not.toBeNull();
    expect(inkCanvas?.getAttribute("tabindex")).toBeNull();

    const css = runtimeCssFor(inkCanvas as Element);
    expect(css).toContain("pointer-events: none");
    expect(css).toContain("position: fixed");
    expect(css).toContain("forced-colors");
  });

  it("keeps the static CSS layers when neither renderer is available", () => {
    const originalCreateElement = document.createElement.bind(document);
    vi.spyOn(document, "createElement").mockImplementation(
      (tagName: string, options?: ElementCreationOptions) => {
        const element = originalCreateElement(tagName, options);

        if (tagName.toLowerCase() === "canvas") {
          vi.spyOn(element as HTMLCanvasElement, "getContext").mockReturnValue(
            null,
          );
        }

        return element;
      },
    );
    const requestFrame = vi
      .spyOn(window, "requestAnimationFrame")
      .mockReturnValue(1);

    mountBackground();

    const inkCanvas = document.querySelector("canvas[aria-hidden='true']");
    const inkFallback = document.querySelector("[data-ink-fallback]");
    expect(inkCanvas).not.toBeNull();
    expect(inkFallback?.tagName).toBe("DIV");
    expect(requestFrame).not.toHaveBeenCalled();

    const css = runtimeCssFor(inkFallback as Element);
    expect(css).toContain("radial-gradient");
  });

  it("keeps the CSS fallback static when neither renderer is available", () => {
    stubMatchMedia((query) => query === "(pointer: fine)");
    const originalCreateElement = document.createElement.bind(document);
    vi.spyOn(document, "createElement").mockImplementation(
      (tagName: string, options?: ElementCreationOptions) => {
        const element = originalCreateElement(tagName, options);

        if (tagName.toLowerCase() === "canvas") {
          vi.spyOn(element as HTMLCanvasElement, "getContext").mockReturnValue(
            null,
          );
        }

        return element;
      },
    );
    Object.defineProperty(window, "innerHeight", {
      configurable: true,
      value: 800,
    });
    Object.defineProperty(window, "innerWidth", {
      configurable: true,
      value: 1000,
    });

    const fallbackLayers = createInkFallbackLayers("light");
    expect(fallbackLayers).not.toContain("--ink-smoke");
    expect(fallbackLayers).not.toContain("--ink-pointer");

    mountBackground();
    window.dispatchEvent(
      new MouseEvent("pointermove", { clientX: 750, clientY: 200 }),
    );

    const inkFallback = document.querySelector<HTMLElement>(
      "[data-ink-fallback]",
    );
    expect(inkFallback).not.toBeNull();
    expect(inkFallback?.style.cssText).toBe("");
  });

  it("animates Canvas2D and hides CSS when webgl2 is unavailable", () => {
    stubMatchMedia((query) => query === "(pointer: fine)");
    const context = stubCanvas2dFallback();
    const frameCallbacks: FrameRequestCallback[] = [];
    const addListener = vi.spyOn(window, "addEventListener");
    vi.spyOn(window, "requestAnimationFrame").mockImplementation((callback) => {
      frameCallbacks.push(callback);
      return frameCallbacks.length;
    });

    const view = mountBackground();
    const canvas = document.querySelector<HTMLCanvasElement>(
      "canvas[data-ink-active='true']",
    );
    const fallback = document.querySelector<HTMLElement>("[data-ink-fallback]");

    expect(frameCallbacks).toHaveLength(1);
    expect(fallback?.style.display).toBe("none");

    let clock = performance.now();
    for (let index = 0; index < 4; index += 1) {
      clock += 16;
      frameCallbacks.at(-1)?.(clock);
    }

    expect(context.putImageData).toHaveBeenCalled();
    expect(canvas?.width).toBeGreaterThan(0);
    expect(canvas?.width).toBeLessThanOrEqual(240);
    expect(addListener).not.toHaveBeenCalledWith(
      "pointermove",
      expect.any(Function),
      expect.anything(),
    );
    expect(addListener).not.toHaveBeenCalledWith(
      "pointerdown",
      expect.any(Function),
      expect.anything(),
    );

    view.unmount();
    expect(context.clearRect).toHaveBeenCalled();
  });

  it("uses a fresh Canvas2D surface when webgl locks the first canvas", () => {
    stubMatchMedia((query) => query === "(pointer: fine)");
    const { context } = stubLockedWebglCanvas();
    const frameCallbacks: FrameRequestCallback[] = [];
    vi.spyOn(window, "requestAnimationFrame").mockImplementation((callback) => {
      frameCallbacks.push(callback);
      return frameCallbacks.length;
    });

    mountBackground();

    const canvases = document.querySelectorAll<HTMLCanvasElement>(
      "canvas[aria-hidden='true']",
    );
    const fallback = document.querySelector<HTMLElement>("[data-ink-fallback]");
    expect(canvases).toHaveLength(2);
    expect(fallback?.style.display).toBe("none");
    expect(frameCallbacks).toHaveLength(1);

    let clock = performance.now();
    for (let index = 0; index < 4; index += 1) {
      clock += 16;
      frameCallbacks.at(-1)?.(clock);
    }

    expect(context.putImageData).toHaveBeenCalled();
    expect(context.canvas?.width).toBeGreaterThan(0);
  });

  it("draws a single static frame and skips the loop with reduced motion", () => {
    stubMatchMedia((query) => query === "(prefers-reduced-motion: reduce)");
    const gl = createStubGl() as unknown as StubGl;
    stubWebglCanvas(gl);
    const requestFrame = vi
      .spyOn(window, "requestAnimationFrame")
      .mockReturnValue(1);

    mountBackground();

    expect(requestFrame).not.toHaveBeenCalled();
    expect(gl.drawArrays).toHaveBeenCalledTimes(1);
  });

  it("redraws the static frame with new colors after a theme change", async () => {
    stubMatchMedia((query) => query === "(prefers-reduced-motion: reduce)");
    const gl = createStubGl() as unknown as StubGl;
    stubWebglCanvas(gl);
    const requestFrame = vi
      .spyOn(window, "requestAnimationFrame")
      .mockReturnValue(1);

    const view = mountBackground("light");
    await waitFor(() => expect(gl.drawArrays).toHaveBeenCalledTimes(1));
    gl.drawArrays.mockClear();
    gl.uniform3f.mockClear();

    view.rerender(
      <VinxThemeProvider>
        <ModeProbe mode="dark" />
        <InkBackground />
      </VinxThemeProvider>,
    );

    await waitFor(() => expect(gl.drawArrays).toHaveBeenCalledTimes(1));
    expect(requestFrame).not.toHaveBeenCalled();
    expect(
      gl.uniform3f.mock.calls.some(
        (call) => Math.abs(call[1] - 0x66 / 255) < 0.002,
      ),
    ).toBe(true);
  });

  it("keeps the ambient loop when there is no fine pointer", () => {
    stubMatchMedia(() => false);
    const gl = createStubGl() as unknown as StubGl;
    stubWebglCanvas(gl);
    const frameCallbacks: FrameRequestCallback[] = [];
    vi.spyOn(window, "requestAnimationFrame").mockImplementation((callback) => {
      frameCallbacks.push(callback);
      return frameCallbacks.length;
    });

    mountBackground();

    expect(frameCallbacks).toHaveLength(1);
    frameCallbacks.at(-1)?.(performance.now() + 16);
    frameCallbacks.at(-1)?.(performance.now() + 32);
    expect(gl.drawArrays).toHaveBeenCalled();
  });

  it("runs ambient smoke without pointer listeners and cleans up", () => {
    stubMatchMedia((query) => query === "(min-width: 900px)");
    const gl = createStubGl() as unknown as StubGl;
    stubWebglCanvas(gl);

    const frameCallbacks: FrameRequestCallback[] = [];
    vi.spyOn(window, "requestAnimationFrame").mockImplementation((callback) => {
      frameCallbacks.push(callback);
      return frameCallbacks.length;
    });
    const cancelFrame = vi
      .spyOn(window, "cancelAnimationFrame")
      .mockImplementation(() => {});
    const addListener = vi.spyOn(window, "addEventListener");
    const removeListener = vi.spyOn(window, "removeEventListener");

    const view = mountBackground();
    const inkFallback = document.querySelector<HTMLElement>(
      "[data-ink-fallback]",
    );

    expect(gl.drawArrays).not.toHaveBeenCalled();
    expect(frameCallbacks.length).toBe(1);
    expect(inkFallback?.style.display).toBe("none");

    let clock = performance.now();
    const step = () => {
      clock += 16;
      const callback = frameCallbacks.at(-1);
      callback?.(clock);
    };

    step();
    step();
    expect(gl.drawArrays).toHaveBeenCalled();
    expect(addListener).not.toHaveBeenCalledWith(
      "pointermove",
      expect.any(Function),
      expect.anything(),
    );
    expect(addListener).not.toHaveBeenCalledWith(
      "pointerdown",
      expect.any(Function),
      expect.anything(),
    );

    view.unmount();

    expect(cancelFrame).toHaveBeenCalled();
    expect(removeListener).not.toHaveBeenCalledWith(
      "pointermove",
      expect.any(Function),
    );
    expect(removeListener).not.toHaveBeenCalledWith(
      "pointerdown",
      expect.any(Function),
    );
    expect(gl.deleteProgram).toHaveBeenCalledTimes(1);
    expect(gl.getExtension).toHaveBeenCalledWith("WEBGL_lose_context");
  });

  it("pauses the loop while the tab is hidden and resumes after", () => {
    stubMatchMedia((query) => query === "(pointer: fine)");
    const gl = createStubGl() as unknown as StubGl;
    stubWebglCanvas(gl);

    const frameCallbacks: FrameRequestCallback[] = [];
    vi.spyOn(window, "requestAnimationFrame").mockImplementation((callback) => {
      frameCallbacks.push(callback);
      return frameCallbacks.length;
    });
    const cancelFrame = vi
      .spyOn(window, "cancelAnimationFrame")
      .mockImplementation(() => {});

    mountBackground();

    Object.defineProperty(document, "hidden", {
      configurable: true,
      value: true,
    });
    document.dispatchEvent(new Event("visibilitychange"));

    expect(cancelFrame).toHaveBeenCalled();

    Object.defineProperty(document, "hidden", {
      configurable: true,
      value: false,
    });
    document.dispatchEvent(new Event("visibilitychange"));

    expect(frameCallbacks.length).toBe(2);
  });

  it("recolors the field when the theme mode changes", async () => {
    stubMatchMedia((query) => query === "(pointer: fine)");
    const gl = createStubGl() as unknown as StubGl;
    stubWebglCanvas(gl);

    const frameCallbacks: FrameRequestCallback[] = [];
    vi.spyOn(window, "requestAnimationFrame").mockImplementation((callback) => {
      frameCallbacks.push(callback);
      return frameCallbacks.length;
    });

    const view = mountBackground("light");
    frameCallbacks.at(-1)?.(performance.now() + 16);
    gl.uniform3f.mockClear();

    view.rerender(
      <VinxThemeProvider>
        <ModeProbe mode="dark" />
        <InkBackground />
      </VinxThemeProvider>,
    );
    frameCallbacks.at(-1)?.(performance.now() + 32);

    const darkTone = gl.uniform3f.mock.calls.find(
      (call) => Math.abs(call[1] - 0x66 / 255) < 0.002,
    );

    expect(darkTone).toBeDefined();
  });
});
