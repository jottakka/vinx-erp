import { Box } from "@mui/material";
import { useColorScheme } from "@mui/material/styles";
import { useEffect, useMemo, useRef } from "react";
import { InkFieldCanvasRenderer } from "./ink-field-canvas-renderer";
import { InkFieldRenderer } from "./ink-field-renderer";
import type { InkFieldCalmZone, InkFieldScene } from "./ink-field-scene";
import {
  createInkFallbackLayers,
  createInkFieldColors,
  type InkFieldMode,
} from "./theme/ink-field-style";

/**
 * Campo de fumaça cobre decorativo do login. WebGL2 sem dependências, com
 * Canvas2D procedural em navegadores incompatíveis. A animação é ambiente,
 * sem resposta ao ponteiro e sem re-render por frame. Movimento reduzido
 * preserva um único quadro estático; forced colors oculta toda a decoração.
 */

const MAX_PIXEL_RATIO = 2;
const QUALITY_SCALES = [0.75, 0.6, 0.45, 0.35] as const;
const SLOW_FRAME_THRESHOLD_MS = 24;
const FAST_FRAME_THRESHOLD_MS = 13;
const SLOW_FRAME_LIMIT = 30;
const FAST_FRAME_LIMIT = 240;
const STATIC_FRAME_TIME = 37;

function prefersReducedMotion() {
  return (
    typeof window.matchMedia === "function" &&
    window.matchMedia("(prefers-reduced-motion: reduce)").matches
  );
}

export function InkBackground() {
  const { mode } = useColorScheme();
  const inkMode: InkFieldMode = mode === "dark" ? "dark" : "light";
  const fallbackRef = useRef<HTMLDivElement | null>(null);
  const webglCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const canvas2dRef = useRef<HTMLCanvasElement | null>(null);
  const rendererRef = useRef<InkFieldCanvasRenderer | InkFieldRenderer | null>(
    null,
  );
  const redrawStaticRef = useRef<(() => void) | null>(null);
  const colors = useMemo(() => createInkFieldColors(inkMode), [inkMode]);
  const colorsRef = useRef(colors);
  colorsRef.current = colors;

  useEffect(() => {
    rendererRef.current?.setColors(colors);
    redrawStaticRef.current?.();
  }, [colors]);

  useEffect(() => {
    const webglCanvas = webglCanvasRef.current;
    const canvas2d = canvas2dRef.current;

    if (!webglCanvas || !canvas2d) {
      return;
    }

    const reduceMotion = prefersReducedMotion();
    const animated = !reduceMotion;
    const webglRenderer = InkFieldRenderer.create(webglCanvas);
    const renderer = webglRenderer ?? InkFieldCanvasRenderer.create(canvas2d);

    if (!renderer) {
      return;
    }

    const activeCanvas = webglRenderer ? webglCanvas : canvas2d;
    const inactiveCanvas = webglRenderer ? canvas2d : webglCanvas;
    activeCanvas.dataset.inkActive = "true";
    inactiveCanvas.style.setProperty("display", "none");
    rendererRef.current = renderer;
    renderer.setColors(colorsRef.current);
    const fallback = fallbackRef.current;
    fallback?.style.setProperty("display", "none");
    const listenerOptions = { passive: true } as const;

    const inkRenderer = renderer;
    const calmQuery = window.matchMedia("(min-width: 900px)");

    const readCalm = (): InkFieldCalmZone =>
      calmQuery.matches
        ? { strength: 0.72, x: 0.735, y: 0.5 }
        : { strength: 0.45, x: 0.5, y: 0.42 };

    let qualityStep = 0;
    let raf = 0;
    let lastFrame = performance.now();
    let elapsed = 0;
    let frameTimeAverage = 16;
    let slowFrames = 0;
    let fastFrames = 0;
    let skipNextFrame = false;

    function applySize() {
      const ratio = Math.min(window.devicePixelRatio || 1, MAX_PIXEL_RATIO);
      inkRenderer.resize(
        window.innerWidth,
        window.innerHeight,
        ratio,
        QUALITY_SCALES[qualityStep],
      );
    }

    function sceneAt(time: number): InkFieldScene {
      return { calm: readCalm(), time };
    }

    function drawStaticFrame() {
      applySize();
      inkRenderer.draw(sceneAt(STATIC_FRAME_TIME));
    }

    function frame(now: number) {
      raf = window.requestAnimationFrame(frame);
      const frameTimeMs = now - lastFrame;
      lastFrame = now;
      const delta = Math.min(frameTimeMs / 1000, 0.05);
      elapsed += delta;

      frameTimeAverage = frameTimeAverage * 0.9 + frameTimeMs * 0.1;
      if (frameTimeAverage > SLOW_FRAME_THRESHOLD_MS) {
        slowFrames += 1;
        fastFrames = 0;
      } else if (frameTimeAverage < FAST_FRAME_THRESHOLD_MS) {
        fastFrames += 1;
        slowFrames = 0;
      } else {
        slowFrames = 0;
        fastFrames = 0;
      }

      if (
        slowFrames > SLOW_FRAME_LIMIT &&
        qualityStep < QUALITY_SCALES.length - 1
      ) {
        qualityStep += 1;
        slowFrames = 0;
      }

      if (fastFrames > FAST_FRAME_LIMIT && qualityStep > 0) {
        qualityStep -= 1;
        fastFrames = 0;
      }

      skipNextFrame = !skipNextFrame;
      if (skipNextFrame) {
        return;
      }

      applySize();
      inkRenderer.draw(sceneAt(elapsed));
    }

    function onResize() {
      if (animated) {
        applySize();
      } else {
        drawStaticFrame();
      }
    }

    function onCalmChange() {
      if (!animated) {
        drawStaticFrame();
      }
    }

    function onVisibilityChange() {
      if (document.hidden) {
        window.cancelAnimationFrame(raf);
        raf = 0;
        return;
      }

      if (animated && raf === 0) {
        lastFrame = performance.now();
        raf = window.requestAnimationFrame(frame);
      }
    }

    if (animated) {
      raf = window.requestAnimationFrame(frame);
    } else {
      redrawStaticRef.current = drawStaticFrame;
      drawStaticFrame();
    }

    window.addEventListener("resize", onResize, listenerOptions);
    calmQuery.addEventListener("change", onCalmChange);
    document.addEventListener("visibilitychange", onVisibilityChange);

    return () => {
      if (raf) {
        window.cancelAnimationFrame(raf);
      }

      window.removeEventListener("resize", onResize);
      calmQuery.removeEventListener("change", onCalmChange);
      document.removeEventListener("visibilitychange", onVisibilityChange);
      inkRenderer.dispose();
      rendererRef.current = null;
      if (redrawStaticRef.current === drawStaticFrame) {
        redrawStaticRef.current = null;
      }
      delete activeCanvas.dataset.inkActive;
      inactiveCanvas.style.removeProperty("display");
      fallback?.style.removeProperty("display");
    };
  }, []);

  return (
    <>
      <Box
        aria-hidden="true"
        data-ink-fallback=""
        ref={fallbackRef}
        sx={{
          backgroundImage: createInkFallbackLayers(inkMode),
          display: "block",
          height: "100%",
          inset: 0,
          pointerEvents: "none",
          position: "fixed",
          width: "100%",
          zIndex: 0,
          "@media (forced-colors: active)": {
            display: "none",
          },
        }}
      />
      <Box
        aria-hidden="true"
        component="canvas"
        data-ink-webgl=""
        ref={webglCanvasRef}
        sx={{
          display: "block",
          height: "100%",
          inset: 0,
          pointerEvents: "none",
          position: "fixed",
          width: "100%",
          zIndex: 0,
          "@media (forced-colors: active)": {
            display: "none",
          },
        }}
      />
      <Box
        aria-hidden="true"
        component="canvas"
        data-ink-canvas2d=""
        ref={canvas2dRef}
        sx={{
          display: "block",
          height: "100%",
          inset: 0,
          pointerEvents: "none",
          position: "fixed",
          width: "100%",
          zIndex: 0,
          "@media (forced-colors: active)": {
            display: "none",
          },
        }}
      />
    </>
  );
}
