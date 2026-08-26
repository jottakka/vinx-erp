import type { SvgIconComponent } from "@mui/icons-material";
import AdminPanelSettingsOutlinedIcon from "@mui/icons-material/AdminPanelSettingsOutlined";
import GroupsOutlinedIcon from "@mui/icons-material/GroupsOutlined";
import HelpOutlinedIcon from "@mui/icons-material/HelpOutlined";
import HomeOutlinedIcon from "@mui/icons-material/HomeOutlined";
import ManageAccountsOutlinedIcon from "@mui/icons-material/ManageAccountsOutlined";
import NotificationsOutlinedIcon from "@mui/icons-material/NotificationsOutlined";
import PaymentsOutlinedIcon from "@mui/icons-material/PaymentsOutlined";
import ScienceOutlinedIcon from "@mui/icons-material/ScienceOutlined";
import SmartToyOutlinedIcon from "@mui/icons-material/SmartToyOutlined";
import SupportAgentOutlinedIcon from "@mui/icons-material/SupportAgentOutlined";
import ViewKanbanOutlinedIcon from "@mui/icons-material/ViewKanbanOutlined";
import { Skeleton, Stack } from "@mui/material";
import { lazy, type ReactElement, Suspense } from "react";
import { AtendimentoPage } from "../features/atendimento/atendimento-page";
import { DemandasPage } from "../features/demandas/demandas-page";
import { NotificationsPage } from "../features/notifications/notifications-page";
import { PaguaiPage } from "../features/paguai/paguai-page";
import { PeoplePage } from "../features/people/people-page";
import { PermissionsPage } from "../features/permissions/permissions-page";
import { PlatformOrganizationPage } from "../features/platform/platform-organization-page";
import {
  type AppRouteCatalogEntry,
  agentPath,
  appRouteCatalog,
  type AppNavigationLink as CatalogNavigationLink,
} from "../shared/routing/app-paths";
import { HelpPage } from "./help-page";
import { HomePage } from "./home-page";
import type { CompanyAppearanceSlot } from "./theme/company-appearance-slot";

export type AppRouteDefinition = AppRouteCatalogEntry & {
  appearanceSlot?: CompanyAppearanceSlot;
  element: ReactElement;
  featured?: boolean;
  icon: SvgIconComponent;
};

export type AppNavigationLink = CatalogNavigationLink & {
  appearanceSlot?: CompanyAppearanceSlot;
  featured?: boolean;
  icon: SvgIconComponent;
};

type AppRouteRenderer = (
  navigationLinks: readonly AppNavigationLink[],
) => ReactElement;

type AppRoutePresentation = {
  appearanceSlot?: CompanyAppearanceSlot;
  featured?: boolean;
  icon: SvgIconComponent;
  render: AppRouteRenderer;
};

type ProductAppPath = (typeof appRouteCatalog)[number]["path"];

const routePresentation: Record<ProductAppPath, AppRoutePresentation> = {
  "/": {
    icon: HomeOutlinedIcon,
    render: (navigationLinks) => <HomePage navigationLinks={navigationLinks} />,
  },
  "/notificacoes": {
    icon: NotificationsOutlinedIcon,
    render: () => <NotificationsPage />,
  },
  "/ajuda": {
    icon: HelpOutlinedIcon,
    render: (navigationLinks) => <HelpPage navigationLinks={navigationLinks} />,
  },
  "/atendimento": {
    appearanceSlot: "beta",
    icon: SupportAgentOutlinedIcon,
    render: () => <AtendimentoPage />,
  },
  "/kanban": {
    appearanceSlot: "gamma",
    icon: ViewKanbanOutlinedIcon,
    render: () => <AtendimentoPage title="Kanban" />,
  },
  "/demandas": {
    appearanceSlot: "beta",
    icon: ViewKanbanOutlinedIcon,
    render: () => <DemandasPage />,
  },
  "/paguai": {
    appearanceSlot: "alpha",
    featured: true,
    icon: PaymentsOutlinedIcon,
    render: () => <PaguaiPage />,
  },
  "/pessoas": {
    appearanceSlot: "neutral",
    icon: GroupsOutlinedIcon,
    render: () => <PeoplePage />,
  },
  "/permissoes": {
    appearanceSlot: "neutral",
    icon: ManageAccountsOutlinedIcon,
    render: () => <PermissionsPage />,
  },
  "/administracao": {
    icon: AdminPanelSettingsOutlinedIcon,
    render: () => <PlatformOrganizationPage />,
  },
};

const productRouteDefinitions = appRouteCatalog.map((route) => ({
  ...route,
  ...routePresentation[route.path],
}));

export const appNavigationLinks: readonly AppNavigationLink[] =
  productRouteDefinitions
    .filter((route) => route.navigation)
    .map(
      ({ appearanceSlot, featured, group, icon, label, navigation, path }) => ({
        appearanceSlot,
        featured,
        group,
        icon,
        label,
        navigation,
        path,
      }),
    );

const productRouteManifest: readonly AppRouteDefinition[] =
  productRouteDefinitions.map(({ render, ...route }) => ({
    ...route,
    element: render(appNavigationLinks),
  }));

function PrototypeRouteFallback() {
  return (
    <Stack
      aria-label="Carregando protótipo"
      role="status"
      spacing={2}
      sx={{ maxWidth: "36rem", py: 4 }}
    >
      <Skeleton aria-hidden variant="rectangular" width="60%" />
      <Skeleton aria-hidden height={24} variant="rectangular" />
      <Skeleton aria-hidden height={120} variant="rectangular" />
      <Skeleton aria-hidden height={120} variant="rectangular" />
    </Stack>
  );
}

function createPrototypeRoutes(): readonly AppRouteDefinition[] {
  const PrototypeIndexPage = lazy(() =>
    import("../features/prototypes/prototype-index-page").then((module) => ({
      default: module.PrototypeIndexPage,
    })),
  );
  const PaguaiPrototypePage = lazy(() =>
    import("../features/paguai/prototype/paguai-prototype-page").then(
      (module) => ({ default: module.PaguaiPrototypePage }),
    ),
  );

  return [
    {
      element: (
        <Suspense fallback={<PrototypeRouteFallback />}>
          <PrototypeIndexPage />
        </Suspense>
      ),
      group: "support",
      icon: ScienceOutlinedIcon,
      label: "Demonstração",
      navigation: true,
      path: "/prototipos",
    },
    {
      element: (
        <Suspense fallback={<PrototypeRouteFallback />}>
          <PaguaiPrototypePage />
        </Suspense>
      ),
      group: "support",
      icon: ScienceOutlinedIcon,
      label: "Paguai (demonstração)",
      navigation: false,
      path: "/prototipos/paguai",
    },
  ];
}

const prototypeRoutes: readonly AppRouteDefinition[] = import.meta.env.DEV
  ? createPrototypeRoutes()
  : [];

function createAgentRoutes(): readonly AppRouteDefinition[] {
  if (!import.meta.env.DEV) {
    return [];
  }

  const AgentPage = lazy(() =>
    import("../features/agent/agent-page").then((module) => ({
      default: module.AgentPage,
    })),
  );

  return [
    {
      element: (
        <Suspense fallback={<PrototypeRouteFallback />}>
          <AgentPage />
        </Suspense>
      ),
      group: "workspace",
      icon: SmartToyOutlinedIcon,
      label: "Agente",
      navigation: true,
      path: agentPath,
    },
  ];
}

const agentRoutes: readonly AppRouteDefinition[] = createAgentRoutes();

export function selectPrototypeRoutes(enabled: boolean) {
  return enabled ? prototypeRoutes : [];
}

export const appRouteManifest: readonly AppRouteDefinition[] = [
  ...productRouteManifest,
  ...selectPrototypeRoutes(import.meta.env.DEV),
  ...agentRoutes,
];
