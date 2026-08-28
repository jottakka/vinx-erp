import { afterEach, describe, expect, it, vi } from "vitest";
import { InkFieldCanvasRenderer } from "./ink-field-canvas-renderer";
import type { InkFieldScene } from "./ink-field-scene";
import { createInkFieldColors } from "./theme/ink-field-style";

type CapturedFrame = {
  data: Uint8ClampedArray;
  height: number;
  width: number;
};

function createStubCanvas() {
  const canvas = document.createElement("canvas");
  const frames: CapturedFrame[] = [];
  const context = {
    canvas,
    clearRect: vi.fn(),
    createImageData: vi.fn((width: number, height: number) => ({
      data: new Uint8ClampedArray(width * height * 4),
      height,
      width,
    })),
    putImageData: vi.fn((image: ImageData) => {
      frames.push({
        data: new Uint8ClampedArray(image.data),
        height: image.height,
        width: image.width,
      });
    }),
  };

  vi.spyOn(canvas, "getContext").mockImplementation((type: string) =>
    type === "2d" ? (context as unknown as CanvasRenderingContext2D) : null,
  );

  return { canvas, context, frames };
}

function restScene(time = 37): InkFieldScene {
  return {
    time,
    calm: { strength: 0.72, x: 0.735, y: 0.5 },
  };
}

function lastFrame(frames: CapturedFrame[]) {
  const frame = frames.at(-1);

  if (!frame) {
    throw new Error("Expected a captured Canvas2D frame");
  }

  return frame;
}

function meanPixelDifference(
  before: CapturedFrame,
  after: CapturedFrame,
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

function compositedDistances(frame: CapturedFrame, paper: readonly number[]) {
  const distances: number[] = [];

  for (let offset = 0; offset < frame.data.length; offset += 4) {
    const alpha = frame.data[offset + 3] / 255;
    const red = mixForTest(paper[0], frame.data[offset], alpha);
    const green = mixForTest(paper[1], frame.data[offset + 1], alpha);
    const blue = mixForTest(paper[2], frame.data[offset + 2], alpha);
    distances.push(
      Math.hypot(red - paper[0], green - paper[1], blue - paper[2]),
    );
  }

  return distances.sort((left, right) => left - right);
}

function mixForTest(from: number, to: number, amount: number) {
  return from + (to - from) * amount;
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("InkFieldCanvasRenderer.create", () => {
  it("returns null when Canvas2D is unavailable", () => {
    const canvas = document.createElement("canvas");
    vi.spyOn(canvas, "getContext").mockReturnValue(null);

    expect(InkFieldCanvasRenderer.create(canvas)).toBeNull();
  });
});

describe("InkFieldCanvasRenderer", () => {
  it("scales down the bounded procedural buffer at lower quality", () => {
    const { canvas } = createStubCanvas();
    const renderer = InkFieldCanvasRenderer.create(canvas);

    renderer?.resize(1000, 800, 2, 0.75);
    const normalWork = canvas.width * canvas.height;

    expect(canvas.width).toBeGreaterThan(0);
    expect(canvas.height).toBeGreaterThan(0);
    expect(canvas.width).toBeLessThanOrEqual(240);
    expect(canvas.height).toBeLessThanOrEqual(160);
    expect(canvas.width * canvas.height).toBeLessThanOrEqual(38_400);
    expect(canvas.width / canvas.height).toBeCloseTo(1000 / 800, 1);

    renderer?.resize(1000, 800, 2, 0.35);

    expect(canvas.width * canvas.height).toBeLessThan(normalWork * 0.5);
  });

  it("throttles the CPU renderer to at most twenty frames per second", () => {
    const { canvas, frames } = createStubCanvas();
    const renderer = InkFieldCanvasRenderer.create(canvas);

    renderer?.resize(1000, 800, 1, 0.75);
    renderer?.setColors(createInkFieldColors("dark"));
    renderer?.draw(restScene(37));
    renderer?.draw(restScene(37.03));

    expect(frames).toHaveLength(1);

    renderer?.draw(restScene(37.051));

    expect(frames).toHaveLength(2);
  });

  it("draws broad, varied smoke that evolves over time", () => {
    const { canvas, frames } = createStubCanvas();
    const renderer = InkFieldCanvasRenderer.create(canvas);

    renderer?.resize(1000, 800, 1, 1);
    renderer?.setColors(createInkFieldColors("dark"));
    renderer?.draw(restScene(37));
    renderer?.draw(restScene(37.2));

    expect(frames).toHaveLength(2);
    const before = frames[0];
    const after = frames[1];
    const alphaBuckets = new Set<number>();
    const activeQuadrants = new Set<number>();

    for (let y = 0; y < before.height; y += 1) {
      for (let x = 0; x < before.width; x += 1) {
        const alpha = before.data[(y * before.width + x) * 4 + 3];
        alphaBuckets.add(Math.round(alpha / 8));

        if (alpha > 20) {
          activeQuadrants.add(
            (x >= before.width / 2 ? 1 : 0) + (y >= before.height / 2 ? 2 : 0),
          );
        }
      }
    }

    expect(alphaBuckets.size).toBeGreaterThan(12);
    expect(activeQuadrants.size).toBeGreaterThanOrEqual(3);
    expect(meanPixelDifference(before, after, () => true)).toBeGreaterThan(0.5);
  });

  it("keeps light smoke structured with visible copper folds", () => {
    const { canvas, frames } = createStubCanvas();
    const renderer = InkFieldCanvasRenderer.create(canvas);
    const colors = createInkFieldColors("light");

    renderer?.resize(1000, 800, 1, 1);
    renderer?.setColors(colors);
    renderer?.draw(restScene(37));

    const frame = lastFrame(frames);
    const paper = colors.paper.map((channel) => channel * 255);
    const distances = compositedDistances(frame, paper);
    const percentile = (fraction: number) =>
      distances[Math.floor((distances.length - 1) * fraction)];
    const quiet = percentile(0.25);
    const middle = percentile(0.5);
    const fold = percentile(0.9);

    expect(fold).toBeGreaterThan(62);
    expect(quiet).toBeLessThan(10);
    expect(fold - middle).toBeGreaterThan(18);
  });

  it("does not draw before colors are set and clears on dispose", () => {
    const { canvas, context, frames } = createStubCanvas();
    const renderer = InkFieldCanvasRenderer.create(canvas);

    renderer?.resize(1000, 800, 1, 1);
    renderer?.draw(restScene());
    expect(frames).toHaveLength(0);

    renderer?.dispose();
    expect(context.clearRect).toHaveBeenCalledWith(
      0,
      0,
      canvas.width,
      canvas.height,
    );
  });
});
