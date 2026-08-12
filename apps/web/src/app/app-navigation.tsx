import type { SvgIconComponent } from "@mui/icons-material";
import HelpOutlinedIcon from "@mui/icons-material/HelpOutlined";
import HomeOutlinedIcon from "@mui/icons-material/HomeOutlined";

export type AppNavigationItem = {
  icon: SvgIconComponent;
  label: string;
  path: string;
};

export const appNavigationItems = [
  {
    icon: HomeOutlinedIcon,
    label: "Início",
    path: "/",
  },
  {
    icon: HelpOutlinedIcon,
    label: "Ajuda",
    path: "/ajuda",
  },
] as const satisfies readonly AppNavigationItem[];

export type AppPath = (typeof appNavigationItems)[number]["path"];

export function isKnownAppPath(value: unknown): value is AppPath {
  return (
    typeof value === "string" &&
    appNavigationItems.some((item) => item.path === value)
  );
}

export function resolveAppPath(value: unknown): AppPath {
  return isKnownAppPath(value) ? value : "/";
}
