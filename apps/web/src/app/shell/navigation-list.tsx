import {
  Box,
  Divider,
  List,
  ListItem,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  ListSubheader,
  Tooltip,
} from "@mui/material";
import { Fragment } from "react";
import { NavLink, useLocation } from "react-router-dom";
import {
  type AppNavigationGroup,
  navigationGroups,
} from "../../shared/routing/app-paths";
import { appRouteManifest } from "../app-route-manifest";

const navigationGroupLabels: Record<AppNavigationGroup, string> = {
  modules: "Módulos",
  support: "Suporte",
  workspace: "Espaço de trabalho",
};

type NavigationListProps = {
  collapsed?: boolean;
  label: string;
  onNavigate?: () => void;
};

export function NavigationList({
  collapsed = false,
  label,
  onNavigate,
}: NavigationListProps) {
  const location = useLocation();

  return (
    <Box aria-label={label} component="nav">
      <List sx={collapsed ? { px: 1.5 } : undefined}>
        {navigationGroups.map((group) => {
          const items = appRouteManifest.filter(
            (route) => route.navigation && route.group === group,
          );

          return (
            <Fragment key={group}>
              {collapsed ? (
                <Divider sx={{ my: 1 }} />
              ) : (
                <ListSubheader>{navigationGroupLabels[group]}</ListSubheader>
              )}
              {items.map((item) => {
                const Icon = item.icon;
                const selected = location.pathname === item.path;
                const button = (
                  <ListItemButton
                    aria-label={collapsed ? item.label : undefined}
                    component={NavLink}
                    onClick={onNavigate}
                    selected={selected}
                    sx={
                      collapsed
                        ? { justifyContent: "center", px: 1.5 }
                        : undefined
                    }
                    to={item.path}
                  >
                    <ListItemIcon sx={collapsed ? { minWidth: 0 } : undefined}>
                      <Icon />
                    </ListItemIcon>
                    {collapsed ? null : <ListItemText primary={item.label} />}
                  </ListItemButton>
                );

                return (
                  <ListItem disablePadding key={item.path}>
                    {collapsed ? (
                      <Tooltip placement="right" title={item.label}>
                        {button}
                      </Tooltip>
                    ) : (
                      button
                    )}
                  </ListItem>
                );
              })}
            </Fragment>
          );
        })}
      </List>
    </Box>
  );
}
