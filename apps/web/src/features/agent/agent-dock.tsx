import CloseOutlinedIcon from "@mui/icons-material/CloseOutlined";
import OpenInNewOutlinedIcon from "@mui/icons-material/OpenInNewOutlined";
import RestartAltOutlinedIcon from "@mui/icons-material/RestartAltOutlined";
import SmartToyOutlinedIcon from "@mui/icons-material/SmartToyOutlined";
import {
  Drawer,
  IconButton,
  Stack,
  Toolbar,
  Typography,
  useMediaQuery,
} from "@mui/material";
import { useNavigate } from "react-router-dom";
import { agentPath } from "../../shared/routing/app-paths";
import { CompanyIdentity } from "../../shared/ui/company-identity";
import { AgentConversation } from "./agent-conversation";
import { useAgentConversation } from "./agent-conversation-provider";

export const AGENT_DOCK_WIDTH = 380;

function DockContent() {
  const { closeDock, companyName, resetConversation, selectedCompanyId } =
    useAgentConversation();
  const navigate = useNavigate();

  return (
    <Stack
      aria-label="Painel do agente"
      component="aside"
      id="vinx-agent-dock"
      sx={{ display: "flex", flexGrow: 1, height: "100%", minHeight: 0, p: 2 }}
    >
      <Stack direction="row" spacing={1} sx={{ alignItems: "center" }}>
        <SmartToyOutlinedIcon />
        <Stack sx={{ flexGrow: 1 }}>
          <Typography component="h2" variant="subtitle1">
            Agente
          </Typography>
          {companyName ? (
            <CompanyIdentity
              companyId={selectedCompanyId}
              companyName={companyName}
            />
          ) : null}
          <Typography color="text.secondary" variant="caption">
            {companyName
              ? "Uma empresa por envio · Consultas independentes"
              : "Selecione uma empresa · Consultas independentes"}
          </Typography>
        </Stack>
        <IconButton
          aria-label="Reiniciar conversa do agente"
          onClick={resetConversation}
        >
          <RestartAltOutlinedIcon />
        </IconButton>
        <IconButton
          aria-label="Abrir página dedicada do agente"
          onClick={() => {
            closeDock();
            navigate(agentPath);
          }}
        >
          <OpenInNewOutlinedIcon />
        </IconButton>
        <IconButton aria-label="Fechar painel do agente" onClick={closeDock}>
          <CloseOutlinedIcon />
        </IconButton>
      </Stack>
      <AgentConversation />
    </Stack>
  );
}

export function AgentDock() {
  const { closeDock, dockOpen } = useAgentConversation();
  const isDesktop = useMediaQuery((theme) => theme.breakpoints.up("md"), {
    noSsr: true,
  });

  return (
    <Drawer
      anchor="right"
      onClose={closeDock}
      open={dockOpen}
      slotProps={{
        paper: {
          sx: {
            boxSizing: "border-box",
            width: isDesktop ? AGENT_DOCK_WIDTH : "min(100vw, 340px)",
          },
        },
      }}
      sx={(theme) =>
        isDesktop
          ? {
              "& .MuiDrawer-paper": {
                visibility: dockOpen ? "visible" : "hidden",
                zIndex: theme.zIndex.appBar - 1,
              },
              pointerEvents: dockOpen ? "auto" : "none",
              zIndex: theme.zIndex.appBar - 1,
            }
          : {}
      }
      variant={isDesktop ? "permanent" : "temporary"}
    >
      <Toolbar />
      {dockOpen ? <DockContent /> : null}
    </Drawer>
  );
}
