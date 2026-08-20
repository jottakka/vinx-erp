import ChevronLeftOutlinedIcon from "@mui/icons-material/ChevronLeftOutlined";
import ChevronRightOutlinedIcon from "@mui/icons-material/ChevronRightOutlined";
import {
  Alert,
  Box,
  Divider,
  Drawer,
  IconButton,
  Link,
  Toolbar,
} from "@mui/material";
import { useState } from "react";
import { Outlet } from "react-router-dom";
import {
  AgentWorkspace,
  useAgentConversation,
} from "../features/agent/agent-conversation-provider";
import { AGENT_DOCK_WIDTH, AgentDock } from "../features/agent/agent-dock";
import { useSession } from "../features/auth/session-provider";
import { NotificationsProvider } from "../features/notifications/notifications-provider";
import { AppHeader } from "./shell/app-header";
import { NavigationList } from "./shell/navigation-list";

export function AppShell() {
  const [mobileNavigationOpen, setMobileNavigationOpen] = useState(false);
  const [desktopNavigationCollapsed, setDesktopNavigationCollapsed] =
    useState(false);
  const { bootstrap, error, isSigningOut, signOut } = useSession();

  function closeMobileNavigation() {
    setMobileNavigationOpen(false);
  }

  function toggleDesktopNavigation() {
    setDesktopNavigationCollapsed((collapsed) => !collapsed);
  }

  return (
    <AgentWorkspace>
      <NotificationsProvider>
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
          <AppHeader
            isSigningOut={isSigningOut}
            mobileNavigationOpen={mobileNavigationOpen}
            onOpenNavigation={() => setMobileNavigationOpen(true)}
            onSignOut={() => void signOut()}
            userName={bootstrap?.user.displayName ?? ""}
          />
          <Drawer
            open
            slotProps={{
              paper: {
                sx: (theme) => ({
                  borderRightColor: "divider",
                  boxSizing: "border-box",
                  display: "flex",
                  flexDirection: "column",
                  transition: theme.transitions.create("width", {
                    duration: theme.transitions.duration.shortest,
                  }),
                  width: desktopNavigationCollapsed
                    ? theme.vinx.layout.collapsedNavigationWidth
                    : theme.vinx.layout.navigationWidth,
                }),
              },
            }}
            sx={{ display: { xs: "none", md: "block" } }}
            variant="permanent"
          >
            <Toolbar />
            <Box sx={{ flexGrow: 1, overflowY: "auto" }}>
              <NavigationList
                collapsed={desktopNavigationCollapsed}
                label="Navegação lateral"
              />
            </Box>
            <Divider />
            <Box
              sx={{
                display: "flex",
                justifyContent: desktopNavigationCollapsed
                  ? "center"
                  : "flex-end",
                p: 0.5,
              }}
            >
              <IconButton
                aria-label={
                  desktopNavigationCollapsed
                    ? "Expandir navegação lateral"
                    : "Recolher navegação lateral"
                }
                onClick={toggleDesktopNavigation}
              >
                {desktopNavigationCollapsed ? (
                  <ChevronRightOutlinedIcon />
                ) : (
                  <ChevronLeftOutlinedIcon />
                )}
              </IconButton>
            </Box>
          </Drawer>
          <Drawer
            onClose={closeMobileNavigation}
            open={mobileNavigationOpen}
            slotProps={{
              paper: {
                id: "vinx-mobile-navigation",
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
          <AgentDock />
          <ShellMain
            error={error}
            navigationCollapsed={desktopNavigationCollapsed}
          />
        </Box>
      </NotificationsProvider>
    </AgentWorkspace>
  );
}

function ShellMain({
  error,
  navigationCollapsed,
}: {
  error: string | null;
  navigationCollapsed: boolean;
}) {
  const { dockOpen } = useAgentConversation();

  return (
    <Box
      component="main"
      id="conteudo-principal"
      sx={(theme) => ({
        boxSizing: "border-box",
        minHeight: "100dvh",
        ml: {
          md: `${navigationCollapsed ? theme.vinx.layout.collapsedNavigationWidth : theme.vinx.layout.navigationWidth}px`,
        },
        p: {
          xs: `${theme.vinx.layout.contentPadding / 2}px`,
          sm: `${theme.vinx.layout.contentPadding}px`,
        },
        mr: { md: dockOpen ? `${AGENT_DOCK_WIDTH}px` : 0 },
        pt: { xs: "72px", sm: "88px" },
        transition: theme.transitions.create("margin", {
          duration: theme.transitions.duration.shortest,
        }),
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
  );
}
