export type Density = "comfortable" | "compact";

const DENSITY_STORAGE_KEY = "vinx.visual-density.v1";

function isDensity(value: string | null): value is Density {
  return value === "comfortable" || value === "compact";
}

export function readDensity(): Density {
  try {
    const value = window.localStorage.getItem(DENSITY_STORAGE_KEY);
    return isDensity(value) ? value : "comfortable";
  } catch {
    return "comfortable";
  }
}

export function writeDensity(value: Density): void {
  try {
    window.localStorage.setItem(DENSITY_STORAGE_KEY, value);
  } catch {
    // Visual preferences are optional when browser storage is unavailable.
  }
}
