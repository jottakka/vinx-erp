import { Alert, Box, Drawer, Link, Toolbar } from "@mui/material";
import { useState } from "react";
import { Outlet } from "react-router-dom";
import { useSession } from "../features/auth/session-provider";
import { AppHeader } from "./shell/app-header";
import { NavigationList } from "./shell/navigation-list";

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
      <AppHeader
        isSigningOut={isSigningOut}
        mobileNavigationOpen={mobileNavigationOpen}
        onOpenNavigation={() => setMobileNavigationOpen(true)}
        onSignOut={() => void signOut()}
      />
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
