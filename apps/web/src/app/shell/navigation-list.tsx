import {
  Box,
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
import { vinxShadow } from "../../app/theme/surface-tokens";
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
      <List
        sx={{
          display: "flex",
          flexDirection: "column",
          gap: 0.25,
          py: 1.5,
          px: collapsed ? 1.5 : 1.25,
        }}
      >
        {navigationGroups.map((group) => {
          const items = appRouteManifest.filter(
            (route) => route.navigation && route.group === group,
          );

          return (
            <Fragment key={group}>
              {collapsed ? null : (
                <ListSubheader
                  disableSticky
                  sx={{ background: "transparent", px: 2, pb: 0.5 }}
                >
                  {navigationGroupLabels[group]}
                </ListSubheader>
              )}
              {items.map((item) => {
                const Icon = item.icon;
                const selected = location.pathname === item.path;
                const button = (
                  <ListItemButton
                    aria-current={selected ? "page" : undefined}
                    aria-label={collapsed ? item.label : undefined}
                    component={NavLink}
                    onClick={onNavigate}
                    selected={selected}
                    sx={(theme) => ({
                      borderRadius: 2.5,
                      minHeight: theme.vinx.layout.controlMinHeight + 4,
                      transition: theme.transitions.create(
                        ["background-color", "color", "padding-left"],
                        { duration: theme.transitions.duration.shortest },
                      ),
                      ...(collapsed
                        ? { justifyContent: "center", px: 1.5 }
                        : {}),
                      ...(selected
                        ? {
                            "&.Mui-selected": {
                              bgcolor: "primary.main",
                              color: "primary.contrastText",
                              boxShadow: vinxShadow.card,
                              "& .MuiListItemIcon-root": {
                                color: "primary.contrastText",
                              },
                            },
                          }
                        : {
                            "&:hover": {
                              bgcolor: "action.hover",
                              pl: collapsed ? undefined : 2.25,
                            },
                          }),
                      "&.Mui-focusVisible": {
                        outline: "2px solid var(--vinx-palette-primary-main)",
                        outlineOffset: -2,
                      },
                    })}
                    to={item.path}
                  >
                    <ListItemIcon
                      sx={
                        collapsed
                          ? { minWidth: 0 }
                          : { minWidth: 40, color: "inherit" }
                      }
                    >
                      <Icon />
                    </ListItemIcon>
                    {collapsed ? null : (
                      <ListItemText
                        primary={item.label}
                        slotProps={{
                          primary: {
                            sx: {
                              fontWeight: selected ? 700 : 500,
                              fontSize: "0.9rem",
                            },
                          },
                        }}
                      />
                    )}
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
