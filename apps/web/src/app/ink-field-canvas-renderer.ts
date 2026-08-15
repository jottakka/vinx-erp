import type { InkFieldScene } from "./ink-field-scene";
import type { InkFieldColors, InkFieldRgb } from "./theme/ink-field-style";

/**
 * Fallback procedural em Canvas2D para navegadores sem WebGL2. Renderiza em
 * baixa resolução e deixa o navegador ampliar o bitmap, mantendo o custo de
 * CPU limitado sem trocar o campo orgânico por um gradiente preso ao cursor.
 */

const MAX_BUFFER_WIDTH = 200;
const MAX_BUFFER_HEIGHT = 144;
const TARGET_SCALE = 0.2;
const MIN_FRAME_INTERVAL = 0.05;
const UINT32_RANGE = 4_294_967_295;

function clamp(value: number, minimum = 0, maximum = 1) {
  return Math.min(Math.max(value, minimum), maximum);
}

function mix(from: number, to: number, amount: number) {
  return from + (to - from) * amount;
}

function smoothstep(edge0: number, edge1: number, value: number) {
  const amount = clamp((value - edge0) / (edge1 - edge0));
  return amount * amount * (3 - 2 * amount);
}

function hash(x: number, y: number) {
  let value = Math.imul(x, 374_761_393) + Math.imul(y, 668_265_263);
  value = Math.imul(value ^ (value >>> 13), 1_274_126_177);
  return ((value ^ (value >>> 16)) >>> 0) / UINT32_RANGE;
}

function valueNoise(x: number, y: number) {
  const cellX = Math.floor(x);
  const cellY = Math.floor(y);
  const fractionX = x - cellX;
  const fractionY = y - cellY;
  const curveX = fractionX * fractionX * (3 - 2 * fractionX);
  const curveY = fractionY * fractionY * (3 - 2 * fractionY);
  const top = mix(hash(cellX, cellY), hash(cellX + 1, cellY), curveX);
  const bottom = mix(
    hash(cellX, cellY + 1),
    hash(cellX + 1, cellY + 1),
    curveX,
  );

  return mix(top, bottom, curveY);
}

function fbm(startX: number, startY: number) {
  let x = startX;
  let y = startY;
  let value = 0;
  let amplitude = 0.5;

  for (let octave = 0; octave < 4; octave += 1) {
    value += amplitude * valueNoise(x, y);
    const rotatedX = x * 0.8 + y * 0.6;
    const rotatedY = -x * 0.6 + y * 0.8;
    x = rotatedX * 2.03 + 7.7;
    y = rotatedY * 2.03 + 7.7;
    amplitude *= 0.52;
  }

  return value;
}

function mixRgb(from: InkFieldRgb, to: InkFieldRgb, amount: number) {
  return [
    mix(from[0], to[0], amount),
    mix(from[1], to[1], amount),
    mix(from[2], to[2], amount),
  ] as const;
}

export class InkFieldCanvasRenderer {
  private disposed = false;
  private colors: InkFieldColors | null = null;
  private frame: ImageData | null = null;
  private lastDrawTime = Number.NEGATIVE_INFINITY;
  private readonly context: CanvasRenderingContext2D;

  private constructor(context: CanvasRenderingContext2D) {
    this.context = context;
  }

  static create(canvas: HTMLCanvasElement): InkFieldCanvasRenderer | null {
    const context = canvas.getContext("2d", { alpha: true });
    return context ? new InkFieldCanvasRenderer(context) : null;
  }

  resize(
    cssWidth: number,
    cssHeight: number,
    _pixelRatio: number,
    scale: number,
  ) {
    const resolutionScale = Math.min(
      TARGET_SCALE * clamp(scale, 0.35, 1),
      MAX_BUFFER_WIDTH / Math.max(cssWidth, 1),
      MAX_BUFFER_HEIGHT / Math.max(cssHeight, 1),
    );
    const width = Math.max(1, Math.round(cssWidth * resolutionScale));
    const height = Math.max(1, Math.round(cssHeight * resolutionScale));
    const { canvas } = this.context;

    if (canvas.width === width && canvas.height === height && this.frame) {
      return;
    }

    canvas.width = width;
    canvas.height = height;
    this.frame = this.context.createImageData(width, height);
    this.lastDrawTime = Number.NEGATIVE_INFINITY;
  }

  setColors(colors: InkFieldColors) {
    this.colors = colors;
    this.lastDrawTime = Number.NEGATIVE_INFINITY;
  }

  draw(scene: InkFieldScene) {
    if (this.disposed || !this.colors || !this.frame) {
      return;
    }

    if (
      scene.time >= this.lastDrawTime &&
      scene.time - this.lastDrawTime < MIN_FRAME_INTERVAL
    ) {
      return;
    }

    this.lastDrawTime = scene.time;
    const { data, height, width } = this.frame;
    const aspect = width / height;
    const fieldTime = scene.time * 2.8;
    const coverage = this.colors.dark ? 0.62 : 0.46;
    const alphaCeiling = this.colors.dark ? 0.68 : 0.52;
    const paperMix = this.colors.dark ? 0.9 : 0.72;

    for (let row = 0; row < height; row += 1) {
      const uvY = 1 - row / Math.max(height - 1, 1);

      for (let column = 0; column < width; column += 1) {
        const uvX = column / Math.max(width - 1, 1);
        const pointX = uvX * aspect;
        const pointY = uvY;
        const restWeight =
          0.55 + 0.45 * clamp(1.15 - uvX * 0.7 - (1 - uvY) * 0.35);
        const noiseX = pointX * 1.35 + fieldTime * 0.021;
        const noiseY = pointY * 1.35 - fieldTime * 0.014;
        const potentialA = fbm(noiseX, noiseY);
        const potentialB = fbm(
          noiseX + 3.1 - fieldTime * 0.011,
          noiseY + 1.7 - fieldTime * 0.009,
        );
        const curlX = potentialB - potentialA;
        const curlY = 1 - potentialA - potentialB;
        const displacementX = curlX * 0.12 * restWeight;
        const displacementY = curlY * 0.12 * restWeight;

        const domainX = pointX + displacementX;
        const domainY = pointY + displacementY;
        const layerA = fbm(
          domainX * 2.6 + potentialA * 2.2,
          domainY * 2.6 + potentialB * 2.2,
        );
        const layerB = fbm(
          domainX * 5 + layerA * 1.4 + fieldTime * 0.02,
          domainY * 5 + layerA * 1.4 - fieldTime * 0.017,
        );
        const layerC = valueNoise(
          domainX * 8.5 + layerB * 1.2 - fieldTime * 0.023,
          domainY * 8.5 + layerB * 1.2 + fieldTime * 0.019,
        );
        let density = layerA * 0.56 + layerB * 0.3 + layerC * 0.14;
        density *= 0.74 + 0.5 * restWeight;
        const shaped = smoothstep(0.4, 0.76, density);
        const ridge = 1 - Math.abs(2 * layerB - 1);
        const sheen =
          smoothstep(0.76, 0.96, ridge) * smoothstep(0.3, 0.72, layerC) * 0.08;
        const calmDeltaX = pointX - scene.calm.x * aspect;
        const calmDeltaY = pointY - scene.calm.y;
        const calm =
          1 -
          scene.calm.strength *
            Math.exp(
              -(calmDeltaX * calmDeltaX + calmDeltaY * calmDeltaY) / 0.2,
            );
        const visibleShape = shaped * calm;
        const visibleSheen = sheen * calm;
        const inkTone = mixRgb(
          this.colors.inkDark,
          this.colors.inkMain,
          clamp(visibleShape * 1.25),
        );
        const litTone = mixRgb(
          inkTone,
          this.colors.inkLight,
          clamp(visibleSheen * 0.65),
        );
        const tone = mixRgb(this.colors.paper, litTone, paperMix);
        const alpha = clamp(
          visibleShape * coverage + visibleSheen * 0.12,
          0,
          alphaCeiling,
        );
        const pixel = (row * width + column) * 4;
        data[pixel] = Math.round(tone[0] * 255);
        data[pixel + 1] = Math.round(tone[1] * 255);
        data[pixel + 2] = Math.round(tone[2] * 255);
        data[pixel + 3] = Math.round(alpha * 255);
      }
    }

    this.context.putImageData(this.frame, 0, 0);
  }

  dispose() {
    if (this.disposed) {
      return;
    }

    this.disposed = true;
    const { canvas } = this.context;
    this.context.clearRect(0, 0, canvas.width, canvas.height);
  }
}
