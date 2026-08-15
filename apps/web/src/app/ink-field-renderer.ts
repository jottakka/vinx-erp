import type { InkFieldScene } from "./ink-field-scene";
import type { InkFieldColors } from "./theme/ink-field-style";

/**
 * Renderizador WebGL2 do campo de tinta cobre. Um único passe: um triângulo
 * de tela cheia (posições derivadas de gl_VertexID, sem buffers) e um shader
 * procedural ambiente com ruído FBM, domain warping e microbrilhos metálicos,
 * sem resposta ao ponteiro. Retorna null quando o contexto ou o programa não
 * podem ser criados, permitindo a tentativa com Canvas2D.
 */

const VERTEX_SOURCE = `#version 300 es
void main() {
  vec2 position = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  gl_Position = vec4(position * 2.0 - 1.0, 0.0, 1.0);
}
`;

const VISIBILITY_PROFILE = {
  dark: { alphaCeiling: 0.55, coverage: 0.5, paperMix: 0.85 },
  light: { alphaCeiling: 0.5, coverage: 0.42, paperMix: 0.72 },
} as const;

const FRAGMENT_SOURCE = `#version 300 es
precision highp float;

uniform vec2 uResolution;
uniform float uTime;
uniform vec3 uInkLight;
uniform vec3 uInkMain;
uniform vec3 uInkDark;
uniform vec3 uPaper;
uniform vec3 uGlint;
uniform float uDark;
uniform float uCoverage;
uniform float uAlphaCeiling;
uniform float uPaperMix;
uniform vec2 uCalmCenter;
uniform float uCalmStrength;

out vec4 fragColor;

float hash(vec2 point) {
  point = fract(point * vec2(127.1, 311.7));
  point += dot(point, point + 34.23);
  return fract(point.x * point.y);
}

float valueNoise(vec2 point) {
  vec2 cell = floor(point);
  vec2 fraction = fract(point);
  vec2 curve = fraction * fraction * (3.0 - 2.0 * fraction);
  float a = hash(cell);
  float b = hash(cell + vec2(1.0, 0.0));
  float c = hash(cell + vec2(0.0, 1.0));
  float d = hash(cell + vec2(1.0, 1.0));
  return mix(mix(a, b, curve.x), mix(c, d, curve.x), curve.y);
}

float fbm(vec2 point) {
  float value = 0.0;
  float amplitude = 0.5;
  mat2 rotate = mat2(0.8, 0.6, -0.6, 0.8);
  for (int octave = 0; octave < 5; octave += 1) {
    value += amplitude * valueNoise(point);
    point = rotate * point * 2.03 + 7.7;
    amplitude *= 0.52;
  }
  return value;
}

void main() {
  vec2 uv = gl_FragCoord.xy / uResolution;
  float aspect = uResolution.x / uResolution.y;
  vec2 p = vec2(uv.x * aspect, uv.y);
  float t = uTime;

  float restWeight = 0.55 + 0.45 * clamp(1.15 - uv.x * 0.7 - (1.0 - uv.y) * 0.35, 0.0, 1.0);

  vec2 noisePoint = p * 1.35 + vec2(t * 0.021, -t * 0.014);
  float potentialA = fbm(noisePoint);
  float potentialB = fbm(noisePoint + vec2(3.1, 1.7) - vec2(t * 0.011, t * 0.009));
  vec2 curl = vec2(potentialB - potentialA, 1.0 - potentialA - potentialB);

  vec2 displacement = curl * 0.08 * restWeight;

  vec2 domain = p + displacement;
  vec2 warp = vec2(
    fbm(domain * 3.2 + vec2(-t * 0.016, t * 0.012)),
    fbm(domain * 3.2 + vec2(5.2 - t * 0.013, 1.3 + t * 0.015))
  );
  float layerA = fbm(domain * 2.6 + warp * 2.2);
  float layerB = fbm(domain * 5.0 + layerA * 1.4 + vec2(t * 0.02, -t * 0.017));
  float layerC = fbm(domain * 8.5 + layerB * 1.2 - t * 0.023);
  float density = layerA * 0.55 + layerB * 0.3 + layerC * 0.15;
  density *= 0.72 + 0.5 * restWeight;
  float shaped = smoothstep(0.46, 0.82, density);

  float ridge = 1.0 - abs(2.0 * layerB - 1.0);
  float ridgeBreak = valueNoise(domain * 6.0 + 11.0);
  float sheen = smoothstep(0.8, 0.97, ridge) * smoothstep(0.3, 0.7, ridgeBreak)
    * 0.08;

  float glintScale = 60.0;
  vec2 glintCell = floor(domain * glintScale);
  vec2 glintFraction = fract(domain * glintScale);
  float glint = smoothstep(0.1, 0.0, length(glintFraction - 0.5))
    * step(0.965, hash(glintCell + 3.7))
    * smoothstep(0.45, 0.7, density)
    * 0.15;

  vec2 calmDelta = p - vec2(uCalmCenter.x * aspect, uCalmCenter.y);
  float calm = 1.0 - uCalmStrength * exp(-dot(calmDelta, calmDelta) / 0.2);
  shaped *= calm;
  sheen *= calm;
  glint *= calm;

  vec3 inkTone = mix(uInkDark, uInkMain, clamp(shaped * 1.25, 0.0, 1.0));
  inkTone = mix(inkTone, uInkLight, clamp(sheen * (0.5 + 0.5 * uDark), 0.0, 1.0) * 0.6);

  float alpha = shaped * uCoverage * 0.85
    + sheen * mix(0.1, 0.16, uDark)
    + glint * 0.45;
  alpha = clamp(alpha, 0.0, uAlphaCeiling);

  vec3 tone = mix(uPaper, inkTone, uPaperMix) + uGlint * glint * 0.4;
  fragColor = vec4(tone, alpha);
}
`;

const CONTEXT_ATTRIBUTES: WebGLContextAttributes = {
  alpha: true,
  antialias: false,
  depth: false,
  desynchronized: true,
  powerPreference: "low-power",
  premultipliedAlpha: false,
  preserveDrawingBuffer: false,
  stencil: false,
};

function compileShader(
  gl: WebGL2RenderingContext,
  type: number,
  source: string,
): WebGLShader | null {
  const shader = gl.createShader(type);

  if (!shader) {
    return null;
  }

  gl.shaderSource(shader, source);
  gl.compileShader(shader);

  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
    gl.deleteShader(shader);
    return null;
  }

  return shader;
}

function createProgram(gl: WebGL2RenderingContext): WebGLProgram | null {
  const vertexShader = compileShader(gl, gl.VERTEX_SHADER, VERTEX_SOURCE);

  if (!vertexShader) {
    return null;
  }

  const fragmentShader = compileShader(gl, gl.FRAGMENT_SHADER, FRAGMENT_SOURCE);

  if (!fragmentShader) {
    gl.deleteShader(vertexShader);
    return null;
  }

  const program = gl.createProgram();

  if (!program) {
    gl.deleteShader(vertexShader);
    gl.deleteShader(fragmentShader);
    return null;
  }

  gl.attachShader(program, vertexShader);
  gl.attachShader(program, fragmentShader);
  gl.linkProgram(program);
  gl.deleteShader(vertexShader);
  gl.deleteShader(fragmentShader);

  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    gl.deleteProgram(program);
    return null;
  }

  return program;
}

export class InkFieldRenderer {
  private disposed = false;
  private bufferWidth = 0;
  private bufferHeight = 0;
  private colors: InkFieldColors | null = null;
  private readonly uniforms: Record<string, WebGLUniformLocation | null> = {};
  private readonly gl: WebGL2RenderingContext;
  private readonly program: WebGLProgram;

  constructor(gl: WebGL2RenderingContext, program: WebGLProgram) {
    this.gl = gl;
    this.program = program;

    for (const name of [
      "uResolution",
      "uTime",
      "uInkLight",
      "uInkMain",
      "uInkDark",
      "uPaper",
      "uGlint",
      "uDark",
      "uCoverage",
      "uAlphaCeiling",
      "uPaperMix",
      "uCalmCenter",
      "uCalmStrength",
    ]) {
      this.uniforms[name] = gl.getUniformLocation(program, name);
    }
  }

  static create(canvas: HTMLCanvasElement): InkFieldRenderer | null {
    const gl = canvas.getContext("webgl2", CONTEXT_ATTRIBUTES);

    if (!gl) {
      return null;
    }

    const program = createProgram(gl);

    if (!program) {
      gl.getExtension("WEBGL_lose_context")?.loseContext();
      return null;
    }

    return new InkFieldRenderer(gl, program);
  }

  resize(
    cssWidth: number,
    cssHeight: number,
    pixelRatio: number,
    scale: number,
  ) {
    const width = Math.max(1, Math.round(cssWidth * pixelRatio * scale));
    const height = Math.max(1, Math.round(cssHeight * pixelRatio * scale));

    if (width === this.bufferWidth && height === this.bufferHeight) {
      return;
    }

    this.bufferWidth = width;
    this.bufferHeight = height;
    this.gl.canvas.width = width;
    this.gl.canvas.height = height;
  }

  setColors(colors: InkFieldColors) {
    this.colors = colors;
  }

  draw(scene: InkFieldScene) {
    if (this.disposed || !this.colors) {
      return;
    }

    const { gl, uniforms } = this;

    // biome-ignore lint/correctness/useHookAtTopLevel: método WebGL, não um hook React.
    gl.useProgram(this.program);
    gl.viewport(0, 0, this.bufferWidth, this.bufferHeight);
    gl.uniform2f(uniforms.uResolution, this.bufferWidth, this.bufferHeight);
    gl.uniform1f(uniforms.uTime, scene.time);
    gl.uniform3f(
      uniforms.uInkLight,
      this.colors.inkLight[0],
      this.colors.inkLight[1],
      this.colors.inkLight[2],
    );
    gl.uniform3f(
      uniforms.uInkMain,
      this.colors.inkMain[0],
      this.colors.inkMain[1],
      this.colors.inkMain[2],
    );
    gl.uniform3f(
      uniforms.uInkDark,
      this.colors.inkDark[0],
      this.colors.inkDark[1],
      this.colors.inkDark[2],
    );
    gl.uniform3f(
      uniforms.uPaper,
      this.colors.paper[0],
      this.colors.paper[1],
      this.colors.paper[2],
    );
    gl.uniform3f(
      uniforms.uGlint,
      this.colors.glint[0],
      this.colors.glint[1],
      this.colors.glint[2],
    );
    gl.uniform1f(uniforms.uDark, this.colors.dark ? 1 : 0);
    const visibility = this.colors.dark
      ? VISIBILITY_PROFILE.dark
      : VISIBILITY_PROFILE.light;
    gl.uniform1f(uniforms.uCoverage, visibility.coverage);
    gl.uniform1f(uniforms.uAlphaCeiling, visibility.alphaCeiling);
    gl.uniform1f(uniforms.uPaperMix, visibility.paperMix);
    gl.uniform2f(uniforms.uCalmCenter, scene.calm.x, scene.calm.y);
    gl.uniform1f(uniforms.uCalmStrength, scene.calm.strength);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }

  dispose() {
    if (this.disposed) {
      return;
    }

    this.disposed = true;
    this.gl.deleteProgram(this.program);
    this.gl.getExtension("WEBGL_lose_context")?.loseContext();
  }
}
