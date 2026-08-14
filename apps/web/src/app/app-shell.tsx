import HelpOutlinedIcon from "@mui/icons-material/HelpOutlined";
import MenuOutlinedIcon from "@mui/icons-material/MenuOutlined";
import {
  Alert,
  AppBar,
  Box,
  Button,
  Drawer,
  IconButton,
  Link,
  List,
  ListItem,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Toolbar,
} from "@mui/material";
import { useState } from "react";
import {
  NavLink,
  Outlet,
  Link as RouterLink,
  useLocation,
} from "react-router-dom";
import { useSession } from "../features/auth/session-provider";
import { AppLogo } from "../shared/ui/app-logo";
import { AppearanceMenu } from "../shared/ui/appearance-menu";
import { appRouteManifest, navigationGroups } from "./app-route-manifest";

function NavigationList({
  label,
  onNavigate,
}: {
  label: string;
  onNavigate?: () => void;
}) {
  const location = useLocation();

  return (
    <Box aria-label={label} component="nav">
      <List>
        {navigationGroups
          .flatMap((group) =>
            appRouteManifest.filter(
              (item) => item.group === group && item.navigation,
            ),
          )
          .map((item) => {
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
      </List>
    </Box>
  );
}

export function AppShell() {
  const [mobileNavigationOpen, setMobileNavigationOpen] = useState(false);
  const { error, isSigningOut, signOut } = useSession();

  function closeMobileNavigation() {
    setMobileNavigationOpen(false);
  }

  return (
    <Box sx={{ bgcolor: "background.default", minHeight: "100dvh" }}>
      <Link
        href="#conteudo-principal"
        sx={{
          left: 8,
          position: "fixed",
          top: -80,
          zIndex: (theme) => theme.zIndex.tooltip + 1,
          "&:focus": {
            bgcolor: "background.paper",
            color: "text.primary",
            p: 1,
            top: 8,
          },
        }}
        underline="none"
      >
        Pular para o conteúdo principal
      </Link>
      <AppBar
        color="transparent"
        elevation={0}
        position="fixed"
        sx={(theme) => ({
          borderBottom: 1,
          borderColor: "divider",
          ml: { md: `${theme.vinx.layout.navigationWidth}px` },
          width: { md: `calc(100% - ${theme.vinx.layout.navigationWidth}px)` },
        })}
      >
        <Toolbar>
          <IconButton
            aria-label="Abrir navegação"
            onClick={() => setMobileNavigationOpen(true)}
            sx={{ display: { md: "none" }, mr: 1 }}
          >
            <MenuOutlinedIcon />
          </IconButton>
          <Box
            aria-label="Ir para início"
            component={RouterLink}
            sx={{ color: "inherit", textDecoration: "none" }}
            to="/"
          >
            <AppLogo compact />
          </Box>
          <Box sx={{ flexGrow: 1 }} />
          <AppearanceMenu />
          <IconButton
            aria-label="Abrir ajuda"
            component={RouterLink}
            to="/ajuda"
          >
            <HelpOutlinedIcon />
          </IconButton>
          <Button
            disabled={isSigningOut}
            onClick={() => void signOut()}
            sx={(theme) => ({ minHeight: theme.vinx.layout.controlMinHeight })}
          >
            {isSigningOut ? "Saindo…" : "Sair"}
          </Button>
        </Toolbar>
      </AppBar>
      <Drawer
        open
        sx={(theme) => ({
          display: { xs: "none", md: "block" },
          "& .MuiDrawer-paper": {
            borderRightColor: "divider",
            boxSizing: "border-box",
            width: theme.vinx.layout.navigationWidth,
          },
        })}
        variant="permanent"
      >
        <Toolbar />
        <NavigationList label="Navegação lateral" />
      </Drawer>
      <Drawer
        onClose={closeMobileNavigation}
        open={mobileNavigationOpen}
        slotProps={{
          paper: {
            sx: (theme) => ({
              boxSizing: "border-box",
              width: theme.vinx.layout.navigationWidth,
            }),
          },
        }}
        sx={{ display: { md: "none" } }}
        variant="temporary"
      >
        <Toolbar />
        <NavigationList
          label="Navegação principal"
          onNavigate={closeMobileNavigation}
        />
      </Drawer>
      <Box
        component="main"
        id="conteudo-principal"
        sx={(theme) => ({
          boxSizing: "border-box",
          minHeight: "100dvh",
          ml: { md: `${theme.vinx.layout.navigationWidth}px` },
          p: {
            xs: `${theme.vinx.layout.contentPadding / 2}px`,
            sm: `${theme.vinx.layout.contentPadding}px`,
          },
          pt: { xs: "72px", sm: "88px" },
        })}
        tabIndex={-1}
      >
        {error ? (
          <Alert
            aria-live="assertive"
            role="alert"
            severity="error"
            sx={{ mb: 3 }}
          >
            {error}
          </Alert>
        ) : null}
        <Outlet />
      </Box>
    </Box>
  );
}
