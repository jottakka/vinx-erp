import { Box } from "@mui/material";
import { useColorScheme } from "@mui/material/styles";
import { useEffect, useMemo, useRef } from "react";
import {
  type InkFieldCalmZone,
  InkFieldModel,
  sceneFromModel,
} from "./ink-field-model";
import { InkFieldRenderer } from "./ink-field-renderer";
import {
  createInkFallbackLayers,
  createInkFieldColors,
  type InkFieldMode,
} from "./theme/ink-field-style";

/**
 * Campo de tinta cobre decorativo do login. WebGL2 sem dependências: o
 * ponteiro alimenta um modelo com inércia (matéria próxima rápida, corpo com
 * atraso) e resposta à velocidade; o shader transforma isso em correntes,
 * rastro e splats dissipativos. Sem re-render por frame — apenas refs,
 * listeners e uniforms. Reduzido a um quadro estático com movimento reduzido
 * ou sem ponteiro fino, oculto em forced colors e substituído pelas camadas
 * CSS quando o WebGL2 não está disponível.
 */

const MAX_PIXEL_RATIO = 2;
const QUALITY_SCALES = [0.75, 0.6, 0.45, 0.35] as const;
const SLOW_FRAME_THRESHOLD_MS = 24;
const FAST_FRAME_THRESHOLD_MS = 13;
const SLOW_FRAME_LIMIT = 30;
const FAST_FRAME_LIMIT = 240;
const IDLE_ENERGY = 0.02;
const STATIC_FRAME_TIME = 37;

function prefersReducedMotion() {
  return (
    typeof window.matchMedia === "function" &&
    window.matchMedia("(prefers-reduced-motion: reduce)").matches
  );
}

function hasFinePointer() {
  return (
    typeof window.matchMedia === "function" &&
    window.matchMedia("(pointer: fine)").matches
  );
}

export function InkBackground() {
  const { mode } = useColorScheme();
  const inkMode: InkFieldMode = mode === "dark" ? "dark" : "light";
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const rendererRef = useRef<InkFieldRenderer | null>(null);
  const colors = useMemo(() => createInkFieldColors(inkMode), [inkMode]);
  const colorsRef = useRef(colors);
  colorsRef.current = colors;

  useEffect(() => {
    rendererRef.current?.setColors(colors);
  }, [colors]);

  useEffect(() => {
    const canvas = canvasRef.current;

    if (!canvas) {
      return;
    }

    const reduceMotion = prefersReducedMotion();
    const finePointer = hasFinePointer();
    const animated = !reduceMotion && finePointer;
    const renderer = InkFieldRenderer.create(canvas);

    if (!renderer) {
      return;
    }

    rendererRef.current = renderer;
    renderer.setColors(colorsRef.current);

    const inkRenderer = renderer;
    const model = new InkFieldModel();
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

    function viewport() {
      return { height: window.innerHeight, width: window.innerWidth };
    }

    function drawStaticFrame() {
      applySize();
      inkRenderer.draw(
        sceneFromModel(model, STATIC_FRAME_TIME, viewport(), readCalm()),
      );
    }

    function frame(now: number) {
      raf = window.requestAnimationFrame(frame);
      const frameTimeMs = now - lastFrame;
      lastFrame = now;
      const delta = Math.min(frameTimeMs / 1000, 0.05);
      elapsed += delta;
      model.update(delta);

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
      if (
        skipNextFrame &&
        model.currentEnergy < IDLE_ENERGY &&
        model.currentFlow < IDLE_ENERGY
      ) {
        return;
      }

      applySize();
      inkRenderer.draw(sceneFromModel(model, elapsed, viewport(), readCalm()));
    }

    function onPointerMove(event: PointerEvent) {
      model.pointer(event.clientX, event.clientY);
    }

    function onPointerDown(event: PointerEvent) {
      model.pulse(event.clientX, event.clientY);
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

    const pointerOptions = { passive: true } as const;

    if (animated) {
      raf = window.requestAnimationFrame(frame);
      window.addEventListener("pointermove", onPointerMove, pointerOptions);
      window.addEventListener("pointerdown", onPointerDown, pointerOptions);
    } else {
      drawStaticFrame();
    }

    window.addEventListener("resize", onResize, pointerOptions);
    calmQuery.addEventListener("change", onCalmChange);
    document.addEventListener("visibilitychange", onVisibilityChange);

    return () => {
      if (raf) {
        window.cancelAnimationFrame(raf);
      }

      if (animated) {
        window.removeEventListener("pointermove", onPointerMove);
        window.removeEventListener("pointerdown", onPointerDown);
      }

      window.removeEventListener("resize", onResize);
      calmQuery.removeEventListener("change", onCalmChange);
      document.removeEventListener("visibilitychange", onVisibilityChange);
      inkRenderer.dispose();
      rendererRef.current = null;
    };
  }, []);

  return (
    <Box
      aria-hidden="true"
      component="canvas"
      ref={canvasRef}
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
  );
}
