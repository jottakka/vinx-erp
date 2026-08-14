import HelpOutlinedIcon from "@mui/icons-material/HelpOutlined";
import MenuOutlinedIcon from "@mui/icons-material/MenuOutlined";
import { AppBar, Box, Button, IconButton, Toolbar } from "@mui/material";
import { Link as RouterLink } from "react-router-dom";
import { AppLogo } from "../../shared/ui/app-logo";
import { AppearanceMenu } from "../../shared/ui/appearance-menu";

type AppHeaderProps = {
  isSigningOut: boolean;
  mobileNavigationOpen: boolean;
  onOpenNavigation: () => void;
  onSignOut: () => void;
};

export function AppHeader({
  isSigningOut,
  mobileNavigationOpen,
  onOpenNavigation,
  onSignOut,
}: AppHeaderProps) {
  return (
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
          aria-controls={
            mobileNavigationOpen ? "vinx-mobile-navigation" : undefined
          }
          aria-expanded={mobileNavigationOpen}
          aria-label="Abrir navegação"
          onClick={onOpenNavigation}
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
        <IconButton aria-label="Abrir ajuda" component={RouterLink} to="/ajuda">
          <HelpOutlinedIcon />
        </IconButton>
        <Button
          disabled={isSigningOut}
          onClick={onSignOut}
          sx={(theme) => ({ minHeight: theme.vinx.layout.controlMinHeight })}
        >
          {isSigningOut ? "Saindo…" : "Sair"}
        </Button>
      </Toolbar>
    </AppBar>
  );
}
