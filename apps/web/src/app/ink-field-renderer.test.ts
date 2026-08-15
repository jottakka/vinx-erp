import { afterEach, describe, expect, it, vi } from "vitest";
import { InkFieldRenderer } from "./ink-field-renderer";
import type { InkFieldScene } from "./ink-field-scene";
import { createInkFieldColors } from "./theme/ink-field-style";

function createStubGl(overrides: Record<string, () => unknown> = {}) {
  const vertexShader = { kind: "vertex" };
  const fragmentShader = { kind: "fragment" };
  const program = { kind: "program" };
  const loseContext = vi.fn();

  return {
    VERTEX_SHADER: 35633,
    FRAGMENT_SHADER: 35632,
    COMPILE_STATUS: 35713,
    LINK_STATUS: 35714,
    TRIANGLES: 4,
    canvas: null as HTMLCanvasElement | null,
    vertexShader,
    fragmentShader,
    program,
    loseContext,
    createShader: vi.fn((type: number) =>
      type === 35633 ? vertexShader : fragmentShader,
    ),
    shaderSource: vi.fn(),
    compileShader: vi.fn(),
    getShaderParameter: vi.fn(() => true),
    createProgram: vi.fn(() => program),
    attachShader: vi.fn(),
    linkProgram: vi.fn(),
    deleteShader: vi.fn(),
    getProgramParameter: vi.fn(() => true),
    deleteProgram: vi.fn(),
    getUniformLocation: vi.fn((_program: unknown, name: string) => ({ name })),
    getExtension: vi.fn(() => ({ loseContext })),
    useProgram: vi.fn(),
    viewport: vi.fn(),
    uniform2f: vi.fn(),
    uniform1f: vi.fn(),
    uniform1i: vi.fn(),
    uniform3f: vi.fn(),
    uniform4fv: vi.fn(),
    drawArrays: vi.fn(),
    ...overrides,
  };
}

function createStubCanvas(gl: ReturnType<typeof createStubGl>) {
  const canvas = document.createElement("canvas");
  gl.canvas = canvas;
  vi.spyOn(canvas, "getContext").mockImplementation((type: string) =>
    type === "webgl2" ? (gl as unknown as WebGL2RenderingContext) : null,
  );
  return canvas;
}

function restScene(): InkFieldScene {
  return {
    time: 37,
    calm: { strength: 0.72, x: 0.735, y: 0.5 },
  };
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("InkFieldRenderer.create", () => {
  it("returns null when webgl2 is unavailable", () => {
    const canvas = document.createElement("canvas");
    vi.spyOn(canvas, "getContext").mockReturnValue(null);

    expect(InkFieldRenderer.create(canvas)).toBeNull();
  });

  it("returns null when the shader fails to compile", () => {
    const gl = createStubGl({ getShaderParameter: () => false });
    const canvas = createStubCanvas(gl);

    expect(InkFieldRenderer.create(canvas)).toBeNull();
    expect(gl.deleteShader).toHaveBeenCalledWith(gl.vertexShader);
    expect(gl.getExtension).toHaveBeenCalledWith("WEBGL_lose_context");
    expect(gl.loseContext).toHaveBeenCalledTimes(1);
  });

  it("returns null when the program fails to link", () => {
    const gl = createStubGl({ getProgramParameter: () => false });
    const canvas = createStubCanvas(gl);

    expect(InkFieldRenderer.create(canvas)).toBeNull();
    expect(gl.deleteShader).toHaveBeenCalledWith(gl.vertexShader);
    expect(gl.deleteShader).toHaveBeenCalledWith(gl.fragmentShader);
    expect(gl.deleteProgram).toHaveBeenCalledWith(gl.program);
    expect(gl.getExtension).toHaveBeenCalledWith("WEBGL_lose_context");
    expect(gl.loseContext).toHaveBeenCalledTimes(1);
  });
});

describe("InkFieldRenderer", () => {
  it("resizes the drawing buffer from ratio and scale", () => {
    const gl = createStubGl();
    const canvas = createStubCanvas(gl);
    const renderer = InkFieldRenderer.create(canvas);

    expect(renderer).not.toBeNull();
    renderer?.resize(1280, 800, 2, 0.75);
    expect(canvas.width).toBe(1920);
    expect(canvas.height).toBe(1200);

    renderer?.resize(1280, 800, 2, 0.75);
    expect(gl.viewport).not.toHaveBeenCalled();
  });

  it("draws a fullscreen pass with the scene and palette uniforms", () => {
    const gl = createStubGl();
    const canvas = createStubCanvas(gl);
    const renderer = InkFieldRenderer.create(canvas);

    renderer?.resize(1280, 800, 1, 0.5);
    renderer?.setColors(createInkFieldColors("light"));
    renderer?.draw(restScene());

    expect(gl.useProgram).toHaveBeenCalled();
    expect(gl.viewport).toHaveBeenCalledWith(0, 0, 640, 400);
    expect(gl.uniform4fv).not.toHaveBeenCalled();
    expect(gl.uniform1i).not.toHaveBeenCalled();
    expect(gl.drawArrays).toHaveBeenCalledWith(gl.TRIANGLES, 0, 3);
  });

  it("skips drawing before palette colors are provided", () => {
    const gl = createStubGl();
    const canvas = createStubCanvas(gl);
    const renderer = InkFieldRenderer.create(canvas);

    renderer?.resize(1280, 800, 1, 1);
    renderer?.draw(restScene());

    expect(gl.drawArrays).not.toHaveBeenCalled();
  });

  it("uploads the theme palette tones as ink uniforms", () => {
    const gl = createStubGl();
    const canvas = createStubCanvas(gl);
    const renderer = InkFieldRenderer.create(canvas);

    renderer?.resize(1280, 800, 1, 1);
    renderer?.setColors(createInkFieldColors("light"));
    renderer?.draw(restScene());

    const toneCalls = gl.uniform3f.mock.calls;
    const mainTone = toneCalls.find(
      (call) => Math.abs(call[1] - 0x92 / 255) < 0.002,
    );

    expect(mainTone).toBeDefined();
    expect(toneCalls.length).toBeGreaterThanOrEqual(5);
  });

  it("uploads the stronger light smoke visibility profile", () => {
    const gl = createStubGl();
    const renderer = InkFieldRenderer.create(createStubCanvas(gl));

    renderer?.resize(1000, 800, 1, 0.5);
    renderer?.setColors(createInkFieldColors("light"));
    renderer?.draw(restScene());

    expect(gl.uniform1f).toHaveBeenCalledWith({ name: "uCoverage" }, 0.42);
    expect(gl.uniform1f).toHaveBeenCalledWith({ name: "uAlphaCeiling" }, 0.5);
    expect(gl.uniform1f).toHaveBeenCalledWith({ name: "uPaperMix" }, 0.72);
  });

  it("recolors the field with the dark palette", () => {
    const gl = createStubGl();
    const canvas = createStubCanvas(gl);
    const renderer = InkFieldRenderer.create(canvas);

    renderer?.resize(1280, 800, 1, 1);
    renderer?.setColors(createInkFieldColors("dark"));
    renderer?.draw(restScene());

    const darkTone = gl.uniform3f.mock.calls.find(
      (call) => Math.abs(call[1] - 0xcd / 255) < 0.002,
    );

    expect(darkTone).toBeDefined();
  });

  it("releases the program and the context on dispose, only once", () => {
    const gl = createStubGl();
    const canvas = createStubCanvas(gl);
    const renderer = InkFieldRenderer.create(canvas);

    renderer?.dispose();
    renderer?.draw(restScene());
    renderer?.dispose();

    expect(gl.deleteProgram).toHaveBeenCalledTimes(1);
    expect(gl.getExtension).toHaveBeenCalledWith("WEBGL_lose_context");
    expect(gl.drawArrays).not.toHaveBeenCalled();
  });
});
