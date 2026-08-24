import { beforeEach, describe, expect, it } from "vitest";
import {
  createVinxStorageManager,
  createVinxTheme,
  vinxTheme,
} from "./create-vinx-theme";
import { vinxFieldBorder } from "./surface-tokens";
import { readDensity } from "./visual-preferences";

function relativeLuminance(color: string) {
  const channels = color
    .match(/^#([\da-f]{6})$/i)?.[1]
    ?.match(/.{2}/g)
    ?.map((channel) => Number.parseInt(channel, 16) / 255);

  if (!channels) {
    throw new Error(`Test color must be a six-digit hex value: ${color}`);
  }

  const linearChannels = channels.map((channel) =>
    channel <= 0.03928 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4,
  );

  return (
    0.2126 * linearChannels[0] +
    0.7152 * linearChannels[1] +
    0.0722 * linearChannels[2]
  );
}

function contrastRatio(foreground: string, background: string) {
  const foregroundLuminance = relativeLuminance(foreground);
  const backgroundLuminance = relativeLuminance(background);
  const lighter = Math.max(foregroundLuminance, backgroundLuminance);
  const darker = Math.min(foregroundLuminance, backgroundLuminance);

  return (lighter + 0.05) / (darker + 0.05);
}

function compositeHex(foreground: string, opacity: number, background: string) {
  const foregroundChannels = foreground
    .match(/^#([\da-f]{6})$/i)?.[1]
    ?.match(/.{2}/g)
    ?.map((channel) => Number.parseInt(channel, 16));
  const backgroundChannels = background
    .match(/^#([\da-f]{6})$/i)?.[1]
    ?.match(/.{2}/g)
    ?.map((channel) => Number.parseInt(channel, 16));

  if (!foregroundChannels || !backgroundChannels) {
    throw new Error("Field contrast colors must be six-digit hex values.");
  }

  return `#${foregroundChannels
    .map((channel, index) => {
      const backgroundChannel = backgroundChannels[index];

      if (backgroundChannel === undefined) {
        throw new Error(
          "Field contrast colors must use matching RGB channels.",
        );
      }

      return Math.round(channel * opacity + backgroundChannel * (1 - opacity));
    })
    .map((channel) => channel.toString(16).padStart(2, "0"))
    .join("")}`;
}

function colorMixOpacity(value: string) {
  const match = value.match(/\s(\d+)%\s*,\s*transparent\)$/);

  if (!match) {
    throw new Error(`Expected a transparent color-mix token: ${value}`);
  }

  const percentage = match[1];

  if (percentage === undefined) {
    throw new Error(`Expected an opacity percentage: ${value}`);
  }

  return Number.parseInt(percentage, 10) / 100;
}

const palettes = [
  ["light", vinxTheme.colorSchemes.light.palette] as const,
  ["dark", vinxTheme.colorSchemes.dark.palette] as const,
];

describe("vinx semantic theme", () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it("maps light and dark schemes to semantic MUI roles", () => {
    expect(vinxTheme.colorSchemes.light.palette.primary.main).toBeDefined();
    expect(
      vinxTheme.colorSchemes.dark.palette.background.default,
    ).toBeDefined();
    expect(vinxTheme.colorSchemes.light.palette.navigation).toBeDefined();
  });

  it("derives smaller usable layout tokens for compact density", () => {
    const comfortable = createVinxTheme("comfortable");
    const compact = createVinxTheme("compact");

    expect(compact.vinx.layout.contentGap).toBeLessThan(
      comfortable.vinx.layout.contentGap,
    );
    expect(compact.vinx.layout.controlMinHeight).toBeGreaterThanOrEqual(24);
  });

  it.each([
    [
      "light text on default",
      vinxTheme.colorSchemes.light.palette.text.primary,
      vinxTheme.colorSchemes.light.palette.background.default,
    ],
    [
      "light text on paper",
      vinxTheme.colorSchemes.light.palette.text.primary,
      vinxTheme.colorSchemes.light.palette.background.paper,
    ],
    [
      "light primary on default",
      vinxTheme.colorSchemes.light.palette.primary.main,
      vinxTheme.colorSchemes.light.palette.background.default,
    ],
    [
      "light primary on paper",
      vinxTheme.colorSchemes.light.palette.primary.main,
      vinxTheme.colorSchemes.light.palette.background.paper,
    ],
    [
      "dark text on default",
      vinxTheme.colorSchemes.dark.palette.text.primary,
      vinxTheme.colorSchemes.dark.palette.background.default,
    ],
    [
      "dark text on paper",
      vinxTheme.colorSchemes.dark.palette.text.primary,
      vinxTheme.colorSchemes.dark.palette.background.paper,
    ],
    [
      "dark primary on default",
      vinxTheme.colorSchemes.dark.palette.primary.main,
      vinxTheme.colorSchemes.dark.palette.background.default,
    ],
    [
      "dark primary on paper",
      vinxTheme.colorSchemes.dark.palette.primary.main,
      vinxTheme.colorSchemes.dark.palette.background.paper,
    ],
  ])("keeps AA contrast for %s", (_name, foreground, background) => {
    expect(contrastRatio(foreground, background)).toBeGreaterThanOrEqual(4.5);
  });

  it("uses valid contrast pairs for every company slot", () => {
    for (const [, palette] of palettes) {
      for (const slot of Object.values(palette.companyAccent)) {
        expect(
          contrastRatio(slot.contrastText, slot.main),
        ).toBeGreaterThanOrEqual(4.5);
      }
    }
  });

  it("uses valid contrast pairs for every status palette", () => {
    for (const [scheme, palette] of palettes) {
      for (const status of [
        "primary",
        "secondary",
        "success",
        "warning",
        "info",
        "error",
      ] as const) {
        expect(
          contrastRatio(palette[status].contrastText, palette[status].main),
          `${scheme} ${status} main/contrastText`,
        ).toBeGreaterThanOrEqual(4.5);
      }
    }
  });

  it.each([
    ["light", vinxTheme.colorSchemes.light.palette, 0.03],
    ["dark", vinxTheme.colorSchemes.dark.palette, 0.04],
  ] as const)(
    "keeps 3:1 non-text contrast on the unfocused %s field boundary",
    (_scheme, palette, fieldFillOpacity) => {
      const fieldFill = compositeHex(
        palette.text.primary,
        fieldFillOpacity,
        palette.background.default,
      );
      const fieldBorder = compositeHex(
        palette.text.primary,
        colorMixOpacity(vinxFieldBorder.rest),
        fieldFill,
      );

      expect(contrastRatio(fieldBorder, fieldFill)).toBeGreaterThanOrEqual(3);
    },
  );

  it("normalizes mode storage defaults and invalid values to light", () => {
    const events = new Map<string, EventListener>();
    const throwingWindow = {
      localStorage: {
        getItem: () => {
          throw new Error("storage blocked");
        },
        setItem: () => {
          throw new Error("storage blocked");
        },
      },
      addEventListener: (type: string, listener: EventListener) => {
        events.set(type, listener);
      },
      removeEventListener: () => {},
    } as unknown as Window;
    const manager = createVinxStorageManager({ key: "vinx.visual.v1" });

    expect(() =>
      manager({ key: "mui-mode", storageWindow: throwingWindow }).set("dark"),
    ).not.toThrow();
    expect(
      manager({ key: "mui-mode", storageWindow: throwingWindow }).get("dark"),
    ).toBe("light");
    expect(
      manager({ key: "mui-mode", storageWindow: throwingWindow }).subscribe(
        () => {},
      ),
    ).not.toThrow();

    const invalidModeWindow = {
      localStorage: {
        getItem: () => "unexpected",
        setItem: () => {},
      },
      addEventListener: () => {},
      removeEventListener: () => {},
    } as unknown as Window;

    const missingModeWindow = {
      localStorage: {
        getItem: () => null,
        setItem: () => {},
      },
      addEventListener: () => {},
      removeEventListener: () => {},
    } as unknown as Window;
    expect(
      manager({ key: "mui-mode", storageWindow: missingModeWindow }).get(
        "dark",
      ),
    ).toBe("light");

    expect(
      manager({ key: "mui-mode", storageWindow: invalidModeWindow }).get(
        "dark",
      ),
    ).toBe("light");

    const legacySystemWindow = {
      ...invalidModeWindow,
      localStorage: { getItem: () => "system", setItem: () => {} },
    } as unknown as Window;
    expect(
      manager({ key: "mui-mode", storageWindow: legacySystemWindow }).get(
        "dark",
      ),
    ).toBe("light");
  });

  it("normalizes invalid mode storage events to light", () => {
    let listener: ((event: StorageEvent) => void) | undefined;
    const modeWindow = {
      localStorage: {
        getItem: () => null,
        setItem: () => {},
      },
      addEventListener: (_type: string, nextListener: EventListener) => {
        listener = nextListener as (event: StorageEvent) => void;
      },
      removeEventListener: () => {},
    } as unknown as Window;
    const manager = createVinxStorageManager({ key: "vinx.visual.v1" });
    const changes: unknown[] = [];

    manager({ key: "mui-mode", storageWindow: modeWindow }).subscribe((value) =>
      changes.push(value),
    );
    listener?.({
      key: "vinx.visual.v1.mui-mode",
      newValue: "system",
    } as StorageEvent);
    listener?.({
      key: "vinx.visual.v1.mui-mode",
      newValue: null,
    } as StorageEvent);

    expect(changes).toEqual(["light", "light"]);
  });

  it("returns MUI defaults for invalid MUI color-scheme keys in reads and subscriptions", () => {
    let listener: ((event: StorageEvent) => void) | undefined;
    const schemeWindow = {
      localStorage: {
        getItem: () => "unexpected",
        setItem: () => {},
      },
      addEventListener: (_type: string, nextListener: EventListener) => {
        listener = nextListener as (event: StorageEvent) => void;
      },
      removeEventListener: () => {},
    } as unknown as Window;
    const manager = createVinxStorageManager({ key: "vinx.visual.v1" });

    for (const storageKey of ["scheme-light", "scheme-dark"]) {
      const schemeStorage = manager({
        key: storageKey,
        storageWindow: schemeWindow,
      });
      const changes: unknown[] = [];

      expect(schemeStorage.get("dark")).toBe("dark");
      schemeStorage.subscribe((value) => changes.push(value));
      listener?.({
        key: `vinx.visual.v1.${storageKey}`,
        newValue: "unexpected",
      } as StorageEvent);

      expect(changes).toEqual([undefined]);
    }
  });

  it("preserves arbitrary values for unrelated storage keys", () => {
    const values = new Map<string, string>();
    const listeners = new Map<string, (event: StorageEvent) => void>();
    const storageWindow = {
      localStorage: {
        getItem: (key: string) => values.get(key) ?? null,
        setItem: (key: string, value: string) => values.set(key, value),
      },
      addEventListener: (_type: string, listener: EventListener) => {
        listeners.set("current", listener as (event: StorageEvent) => void);
      },
      removeEventListener: () => {},
    } as unknown as Window;
    const manager = createVinxStorageManager({ key: "vinx.visual.v1" });

    for (const storageKey of ["audit-mode", "report-light", "report-dark"]) {
      const prefixedKey = `vinx.visual.v1.${storageKey}`;
      const arbitraryValue = `${storageKey}-arbitrary`;
      values.set(prefixedKey, arbitraryValue);
      const storage = manager({ key: storageKey, storageWindow });
      const changes: unknown[] = [];

      expect(storage.get("fallback")).toBe(arbitraryValue);
      storage.subscribe((value) => changes.push(value));
      listeners.get("current")?.({
        key: prefixedKey,
        newValue: arbitraryValue,
      } as StorageEvent);

      expect(changes).toEqual([arbitraryValue]);
    }
  });

  it("falls back to comfortable density when storage is malformed", () => {
    window.localStorage.setItem("vinx.visual-density.v1", "unexpected");

    expect(readDensity()).toBe("comfortable");
  });
});
