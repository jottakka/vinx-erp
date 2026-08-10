import { beforeEach, describe, expect, it } from "vitest";
import { createVinxStorageManager, vinxTheme } from "./create-vinx-theme";
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

const palettes = [
  vinxTheme.colorSchemes.light.palette,
  vinxTheme.colorSchemes.dark.palette,
];
const light = vinxTheme.colorSchemes.light.palette;

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

  it("uses valid contrast pairs for text, focus and every company slot", () => {
    expect(
      contrastRatio(light.text.primary, light.background.default),
    ).toBeGreaterThanOrEqual(4.5);
    expect(
      contrastRatio(light.primary.main, light.background.default),
    ).toBeGreaterThanOrEqual(3);
    for (const palette of palettes) {
      for (const slot of Object.values(palette.companyAccent)) {
        expect(
          contrastRatio(slot.contrastText, slot.main),
        ).toBeGreaterThanOrEqual(4.5);
      }
    }
  });

  it("returns MUI defaults when storage is blocked or contains an invalid mode", () => {
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
      manager({ key: "mui-mode", storageWindow: throwingWindow }).get("system"),
    ).toBe("system");
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

    expect(
      manager({ key: "mui-mode", storageWindow: invalidModeWindow }).get(
        "system",
      ),
    ).toBe("system");
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

      expect(schemeStorage.get("light")).toBe("light");
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
