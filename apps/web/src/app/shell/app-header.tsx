import HelpOutlinedIcon from "@mui/icons-material/HelpOutlined";
import MenuOutlinedIcon from "@mui/icons-material/MenuOutlined";
import NotificationsOutlinedIcon from "@mui/icons-material/NotificationsOutlined";
import {
  AppBar,
  Avatar,
  Badge,
  Box,
  Button,
  Chip,
  IconButton,
  Toolbar,
  useScrollTrigger,
} from "@mui/material";
import { Link as RouterLink } from "react-router-dom";
import { isAgentWorkspaceEnabled } from "../../features/agent/agent-conversation-provider";
import { AgentTrigger } from "../../features/agent/agent-trigger";
import { useNotifications } from "../../features/notifications/notifications-provider";
import { AppLogo } from "../../shared/ui/app-logo";
import { AppearanceMenu } from "../../shared/ui/appearance-menu";

type AppHeaderProps = {
  isSigningOut: boolean;
  mobileNavigationOpen: boolean;
  onOpenNavigation: () => void;
  onSignOut: () => void;
  userName: string;
};

function initialsFromName(name: string) {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? "")
    .join("");
}

export function AppHeader({
  isSigningOut,
  mobileNavigationOpen,
  onOpenNavigation,
  onSignOut,
  userName,
}: AppHeaderProps) {
  const elevated = useScrollTrigger({ disableHysteresis: true, threshold: 8 });
  const { unreadCount } = useNotifications();
  const unreadLabel =
    unreadCount === null
      ? "Notificações"
      : unreadCount === 0
        ? "Notificações: nenhuma não lida"
        : `Notificações: ${unreadCount} não lidas`;

  return (
    <AppBar
      color={elevated ? "default" : "transparent"}
      elevation={elevated ? 4 : 0}
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
        <IconButton
          aria-label={unreadLabel}
          component={RouterLink}
          sx={{ mr: 0.5 }}
          to="/notificacoes"
        >
          <Badge
            badgeContent={unreadCount ?? 0}
            color="error"
            invisible={!(unreadCount && unreadCount > 0)}
          >
            <NotificationsOutlinedIcon />
          </Badge>
        </IconButton>
        {userName ? (
          <Chip
            aria-label={`Sessão ativa: ${userName}`}
            avatar={
              <Avatar
                sx={(theme) => ({
                  bgcolor: "primary.dark",
                  fontSize: theme.typography.body2.fontSize,
                })}
              >
                {initialsFromName(userName)}
              </Avatar>
            }
            label={userName}
            sx={{ mr: 1, maxWidth: { xs: "8rem", sm: "none" } }}
          />
        ) : null}
        <AppearanceMenu />
        {isAgentWorkspaceEnabled() ? <AgentTrigger /> : null}
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
