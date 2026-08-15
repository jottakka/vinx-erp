import { useColorScheme } from "@mui/material/styles";
import { render, screen } from "@testing-library/react";
import { useEffect } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { InkBackground } from "./ink-background";
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
        gl.canvas = canvas;
        vi.spyOn(canvas, "getContext").mockImplementation((type: string) =>
          type === "webgl2" ? (gl as unknown as WebGL2RenderingContext) : null,
        );
      }

      return element;
    },
  );
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

    const inkCanvas = document.querySelector("canvas[aria-hidden='true']");

    expect(inkCanvas).not.toBeNull();
    expect(inkCanvas?.getAttribute("tabindex")).toBeNull();

    const css = runtimeCssFor(inkCanvas as Element);
    expect(css).toContain("pointer-events: none");
    expect(css).toContain("position: fixed");
    expect(css).toContain("forced-colors");
  });

  it("keeps the static CSS layers when webgl2 is unavailable", () => {
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
    expect(inkCanvas).not.toBeNull();
    expect(requestFrame).not.toHaveBeenCalled();

    const css = runtimeCssFor(inkCanvas as Element);
    expect(css).toContain("radial-gradient");
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

  it("draws a single static frame when there is no fine pointer", () => {
    stubMatchMedia(() => false);
    const gl = createStubGl() as unknown as StubGl;
    stubWebglCanvas(gl);
    const requestFrame = vi
      .spyOn(window, "requestAnimationFrame")
      .mockReturnValue(1);

    mountBackground();

    expect(requestFrame).not.toHaveBeenCalled();
    expect(gl.drawArrays).toHaveBeenCalledTimes(1);
  });

  it("runs the animated loop, reacts to the pointer and cleans up on unmount", () => {
    stubMatchMedia(
      (query) => query === "(pointer: fine)" || query === "(min-width: 900px)",
    );
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
    const removeListener = vi.spyOn(window, "removeEventListener");

    const view = mountBackground();

    expect(gl.drawArrays).not.toHaveBeenCalled();
    expect(frameCallbacks.length).toBe(1);

    let clock = performance.now();
    const step = () => {
      clock += 16;
      const callback = frameCallbacks.at(-1);
      callback?.(clock);
    };

    step();
    step();
    expect(gl.drawArrays).toHaveBeenCalled();

    for (let x = 40; x <= 400; x += 40) {
      window.dispatchEvent(
        new MouseEvent("pointermove", { clientX: x, clientY: 300 }),
      );
      step();
    }

    const splatCall = gl.uniform4fv.mock.calls.at(-1);
    const splats = splatCall?.[1] as Float32Array;
    expect(Array.from(splats).some((value) => value > 0)).toBe(true);

    view.unmount();

    expect(cancelFrame).toHaveBeenCalled();
    expect(removeListener).toHaveBeenCalledWith(
      "pointermove",
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
      (call) => Math.abs(call[1] - 0xcd / 255) < 0.002,
    );

    expect(darkTone).toBeDefined();
  });
});
