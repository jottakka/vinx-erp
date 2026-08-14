import {
  Box,
  List,
  ListItem,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  ListSubheader,
} from "@mui/material";
import { Fragment } from "react";
import { NavLink, useLocation } from "react-router-dom";
import { appRouteManifest, navigationGroups } from "../app-route-manifest";

type NavigationGroupId = (typeof navigationGroups)[number];

const navigationGroupLabels: Record<NavigationGroupId, string> = {
  modules: "Módulos",
  support: "Suporte",
  workspace: "Espaço de trabalho",
};

type NavigationListProps = {
  label: string;
  onNavigate?: () => void;
};

export function NavigationList({ label, onNavigate }: NavigationListProps) {
  const location = useLocation();

  return (
    <Box aria-label={label} component="nav">
      <List>
        {navigationGroups.map((group) => {
          const items = appRouteManifest.filter(
            (route) => route.navigation && route.group === group,
          );

          return (
            <Fragment key={group}>
              <ListSubheader component="div">
                {navigationGroupLabels[group]}
              </ListSubheader>
              {items.map((item) => {
                const Icon = item.icon;

                return (
                  <ListItem disablePadding key={item.path}>
                    <ListItemButton
                      component={NavLink}
                      onClick={onNavigate}
                      selected={location.pathname === item.path}
                      to={item.path}
                    >
                      <ListItemIcon>
                        <Icon />
                      </ListItemIcon>
                      <ListItemText primary={item.label} />
                    </ListItemButton>
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
