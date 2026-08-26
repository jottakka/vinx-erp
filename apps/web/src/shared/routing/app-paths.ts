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
  {
    group: "workspace",
    label: "Notificações",
    navigation: true,
    path: "/notificacoes",
  },
  { group: "modules", label: "Paguai", navigation: true, path: "/paguai" },
  {
    group: "modules",
    label: "Atendimento",
    navigation: true,
    path: "/atendimento",
  },
  { group: "modules", label: "Pessoas", navigation: true, path: "/pessoas" },
  {
    group: "support",
    label: "Administração",
    navigation: false,
    path: "/administracao",
  },
  { group: "support", label: "Ajuda", navigation: true, path: "/ajuda" },
] as const satisfies readonly AppRouteCatalogEntry[];

export const prototypePath = "/prototipos" as const;
export const paguaiPrototypePath = "/prototipos/paguai" as const;
export const agentPath = "/agente" as const;

export type AppPath =
  | (typeof appRouteCatalog)[number]["path"]
  | typeof prototypePath
  | typeof paguaiPrototypePath
  | typeof agentPath;
export type AppNavigationLink = Pick<
  AppRouteCatalogEntry,
  "group" | "label" | "navigation" | "path"
>;

export const APP_PATHS: readonly AppPath[] = [
  ...appRouteCatalog.map((route) => route.path),
  prototypePath,
  paguaiPrototypePath,
  agentPath,
];

export function isKnownAppPath(value: unknown): value is AppPath {
  return typeof value === "string" && APP_PATHS.includes(value as AppPath);
}

export function resolveAppPath(value: unknown): AppPath {
  return isKnownAppPath(value) ? value : "/";
}
