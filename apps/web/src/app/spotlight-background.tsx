import { Box } from "@mui/material";
import { useEffect, useRef } from "react";
import { createSpotlightLayers } from "./theme/spotlight-style";

/**
 * Fundo decorativo com brilho radial que acompanha o ponteiro. Atualiza
 * apenas variáveis CSS via requestAnimationFrame (sem re-render) e fica
 * estático quando o usuário prefere movimento reduzido.
 */
export function SpotlightBackground() {
  const surfaceRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const surface = surfaceRef.current;

    if (!surface) {
      return;
    }

    const reduceMotion =
      typeof window.matchMedia === "function" &&
      window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    if (reduceMotion) {
      return;
    }

    let frame = 0;

    function trackPointer(event: PointerEvent) {
      if (frame) {
        return;
      }

      frame = window.requestAnimationFrame(() => {
        frame = 0;
        surfaceRef.current?.style.setProperty(
          "--vinx-spot-x",
          `${event.clientX}px`,
        );
        surfaceRef.current?.style.setProperty(
          "--vinx-spot-y",
          `${event.clientY}px`,
        );
      });
    }

    window.addEventListener("pointermove", trackPointer, { passive: true });

    return () => {
      window.removeEventListener("pointermove", trackPointer);

      if (frame) {
        window.cancelAnimationFrame(frame);
      }
    };
  }, []);

  return (
    <Box
      aria-hidden="true"
      ref={surfaceRef}
      sx={(theme) => ({
        "--vinx-spot-x": "50vw",
        "--vinx-spot-y": "24vh",
        backgroundImage: createSpotlightLayers(theme),
        inset: 0,
        pointerEvents: "none",
        position: "fixed",
        zIndex: 0,
        "@media (forced-colors: active)": {
          display: "none",
        },
      })}
    />
  );
}
