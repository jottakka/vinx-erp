import type { SvgIconComponent } from "@mui/icons-material";
import GroupsOutlinedIcon from "@mui/icons-material/GroupsOutlined";
import HelpOutlinedIcon from "@mui/icons-material/HelpOutlined";
import HomeOutlinedIcon from "@mui/icons-material/HomeOutlined";
import PaymentsOutlinedIcon from "@mui/icons-material/PaymentsOutlined";
import SupportAgentOutlinedIcon from "@mui/icons-material/SupportAgentOutlined";
import ViewKanbanOutlinedIcon from "@mui/icons-material/ViewKanbanOutlined";
import type { ReactElement } from "react";
import {
  type AppPath,
  type AppRouteCatalogEntry,
  appRouteCatalog,
  type AppNavigationLink as CatalogNavigationLink,
} from "../shared/routing/app-paths";
import { FeaturePreparationPage } from "./feature-preparation-page";
import { HelpPage } from "./help-page";
import { HomePage } from "./home-page";

export type AppRouteDefinition = AppRouteCatalogEntry & {
  element: ReactElement;
  icon: SvgIconComponent;
};

export type AppNavigationLink = CatalogNavigationLink & {
  icon: SvgIconComponent;
};

type AppRouteRenderer = (
  navigationLinks: readonly AppNavigationLink[],
) => ReactElement;

type AppRoutePresentation = {
  icon: SvgIconComponent;
  render: AppRouteRenderer;
};

const routePresentation: Record<AppPath, AppRoutePresentation> = {
  "/": {
    icon: HomeOutlinedIcon,
    render: (navigationLinks) => <HomePage navigationLinks={navigationLinks} />,
  },
  "/ajuda": {
    icon: HelpOutlinedIcon,
    render: (navigationLinks) => <HelpPage navigationLinks={navigationLinks} />,
  },
  "/paguai": {
    icon: PaymentsOutlinedIcon,
    render: () => (
      <FeaturePreparationPage
        feature="Paguai"
        scope="Solicitações, conferência e fluxo financeiro."
      />
    ),
  },
  "/atendimento": {
    icon: SupportAgentOutlinedIcon,
    render: () => (
      <FeaturePreparationPage
        feature="Atendimento"
        scope="Fila, tickets e histórico de atendimento."
      />
    ),
  },
  "/kanban": {
    icon: ViewKanbanOutlinedIcon,
    render: () => (
      <FeaturePreparationPage
        feature="Kanban"
        scope="Quadros, cards e acompanhamento do trabalho."
      />
    ),
  },
  "/pessoas": {
    icon: GroupsOutlinedIcon,
    render: () => (
      <FeaturePreparationPage
        feature="Pessoas"
        scope="Diretório, ausências e visão de equipe."
      />
    ),
  },
};

const appRouteDefinitions = appRouteCatalog.map((route) => ({
  ...route,
  ...routePresentation[route.path],
}));

export const appNavigationLinks: readonly AppNavigationLink[] =
  appRouteDefinitions
    .filter((route) => route.navigation)
    .map(({ group, icon, label, navigation, path }) => ({
      group,
      icon,
      label,
      navigation,
      path,
    }));

export const appRouteManifest: readonly AppRouteDefinition[] =
  appRouteDefinitions.map(({ render, ...route }) => ({
    ...route,
    element: render(appNavigationLinks),
  }));
