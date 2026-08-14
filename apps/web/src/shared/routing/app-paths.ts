export const APP_PATHS = [
  "/",
  "/ajuda",
  "/paguai",
  "/atendimento",
  "/kanban",
  "/pessoas",
] as const;

export type AppPath = (typeof APP_PATHS)[number];

export function isKnownAppPath(value: unknown): value is AppPath {
  return typeof value === "string" && APP_PATHS.includes(value as AppPath);
}

export function resolveAppPath(value: unknown): AppPath {
  return isKnownAppPath(value) ? value : "/";
}
