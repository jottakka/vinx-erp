export const navigationGroups = ["workspace", "modules", "support"] as const;

export type AppNavigationGroup = (typeof navigationGroups)[number];

export type AppRouteCatalogEntry = {
  group: AppNavigationGroup;
  label: string;
  navigation: boolean;
  path: string;
};

export const appRouteCatalog = [
  { group: "workspace", label: "Início", navigation: true, path: "/" },
  { group: "modules", label: "Paguai", navigation: true, path: "/paguai" },
  {
    group: "modules",
    label: "Atendimento",
    navigation: true,
    path: "/atendimento",
  },
  { group: "modules", label: "Kanban", navigation: true, path: "/kanban" },
  { group: "modules", label: "Pessoas", navigation: true, path: "/pessoas" },
  { group: "support", label: "Ajuda", navigation: true, path: "/ajuda" },
] as const satisfies readonly AppRouteCatalogEntry[];

export type AppPath = (typeof appRouteCatalog)[number]["path"];
export type AppNavigationLink = Pick<
  AppRouteCatalogEntry,
  "group" | "label" | "navigation" | "path"
>;

export const APP_PATHS: readonly AppPath[] = appRouteCatalog.map(
  (route) => route.path,
);

export function isKnownAppPath(value: unknown): value is AppPath {
  return typeof value === "string" && APP_PATHS.includes(value as AppPath);
}

export function resolveAppPath(value: unknown): AppPath {
  return isKnownAppPath(value) ? value : "/";
}
