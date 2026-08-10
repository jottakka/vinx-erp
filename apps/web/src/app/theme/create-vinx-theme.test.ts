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

  it("uses valid contrast pairs for text, focus and company slots", () => {
    expect(
      contrastRatio(light.text.primary, light.background.default),
    ).toBeGreaterThanOrEqual(4.5);
    expect(
      contrastRatio(light.primary.main, light.background.default),
    ).toBeGreaterThanOrEqual(3);
    expect(
      contrastRatio(
        light.companyAccent.alpha.contrastText,
        light.companyAccent.alpha.main,
      ),
    ).toBeGreaterThanOrEqual(4.5);
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

  it("falls back to comfortable density when storage is malformed", () => {
    window.localStorage.setItem("vinx.visual-density.v1", "unexpected");

    expect(readDensity()).toBe("comfortable");
  });
});
