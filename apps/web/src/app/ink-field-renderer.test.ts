import { afterEach, describe, expect, it, vi } from "vitest";
import { InkFieldModel, sceneFromModel } from "./ink-field-model";
import { InkFieldRenderer } from "./ink-field-renderer";
import { createInkFieldColors } from "./theme/ink-field-style";

function createStubGl(overrides: Record<string, () => unknown> = {}) {
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

function restScene() {
  return sceneFromModel(
    new InkFieldModel(),
    37,
    {
      height: 800,
      width: 1280,
    },
    { strength: 0.72, x: 0.735, y: 0.5 },
  );
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
  });

  it("returns null when the program fails to link", () => {
    const gl = createStubGl({ getProgramParameter: () => false });
    const canvas = createStubCanvas(gl);

    expect(InkFieldRenderer.create(canvas)).toBeNull();
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
    expect(gl.uniform4fv).toHaveBeenCalledWith(
      expect.anything(),
      expect.any(Float32Array),
    );
    expect(gl.uniform1i).toHaveBeenCalled();
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
