import type { SvgIconComponent } from "@mui/icons-material";
import GroupsOutlinedIcon from "@mui/icons-material/GroupsOutlined";
import HelpOutlinedIcon from "@mui/icons-material/HelpOutlined";
import HomeOutlinedIcon from "@mui/icons-material/HomeOutlined";
import PaymentsOutlinedIcon from "@mui/icons-material/PaymentsOutlined";
import SupportAgentOutlinedIcon from "@mui/icons-material/SupportAgentOutlined";
import ViewKanbanOutlinedIcon from "@mui/icons-material/ViewKanbanOutlined";
import type { ReactElement } from "react";
import type { AppPath } from "../shared/routing/app-paths";
import { FeaturePreparationPage } from "./feature-preparation-page";
import { HelpPage } from "./help-page";
import { HomePage } from "./home-page";

export const navigationGroups = ["workspace", "modules", "support"] as const;

type AppNavigationGroup = (typeof navigationGroups)[number];

export type AppRouteDefinition = {
  path: AppPath;
  label: string;
  group: AppNavigationGroup;
  icon: SvgIconComponent;
  element: ReactElement;
  navigation: boolean;
};

export const appRouteManifest: readonly AppRouteDefinition[] = [
  {
    path: "/",
    label: "Início",
    group: "workspace",
    icon: HomeOutlinedIcon,
    element: <HomePage />,
    navigation: true,
  },
  {
    path: "/paguai",
    label: "Paguai",
    group: "modules",
    icon: PaymentsOutlinedIcon,
    element: (
      <FeaturePreparationPage
        feature="Paguai"
        scope="Solicitações, conferência e fluxo financeiro."
      />
    ),
    navigation: true,
  },
  {
    path: "/atendimento",
    label: "Atendimento",
    group: "modules",
    icon: SupportAgentOutlinedIcon,
    element: (
      <FeaturePreparationPage
        feature="Atendimento"
        scope="Fila, tickets e histórico de atendimento."
      />
    ),
    navigation: true,
  },
  {
    path: "/kanban",
    label: "Kanban",
    group: "modules",
    icon: ViewKanbanOutlinedIcon,
    element: (
      <FeaturePreparationPage
        feature="Kanban"
        scope="Quadros, cards e acompanhamento do trabalho."
      />
    ),
    navigation: true,
  },
  {
    path: "/pessoas",
    label: "Pessoas",
    group: "modules",
    icon: GroupsOutlinedIcon,
    element: (
      <FeaturePreparationPage
        feature="Pessoas"
        scope="Diretório, ausências e visão de equipe."
      />
    ),
    navigation: true,
  },
  {
    path: "/ajuda",
    label: "Ajuda",
    group: "support",
    icon: HelpOutlinedIcon,
    element: <HelpPage />,
    navigation: true,
  },
] as const;
