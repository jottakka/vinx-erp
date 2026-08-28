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
    group: "modules",
    label: "Permissões",
    navigation: true,
    path: "/permissoes",
  },
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
export const paguaiPath = "/paguai" as const;

const canonicalUuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

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

/** Builds the only supported Paguai proposal-review deep link. */
export function paguaiProposalPath(proposalId: string): string {
  return `${paguaiPath}?proposal=${encodeURIComponent(proposalId)}`;
}

/** Accepts only the opaque, canonical proposal identifier from Paguai's query. */
export function readPaguaiProposalId(search: string): string | null {
  const value = new URLSearchParams(search).get("proposal");
  return value && canonicalUuidPattern.test(value) ? value : null;
}
