import SmartToyOutlinedIcon from "@mui/icons-material/SmartToyOutlined";
import { IconButton, Tooltip } from "@mui/material";
import { useAgentConversation } from "./agent-conversation-provider";

export function AgentTrigger() {
  const { access, dockOpen, openDock } = useAgentConversation();
  const unavailable = !access.available;
  const label = unavailable
    ? "Abrir painel do agente — indisponível para este perfil"
    : "Abrir painel do agente";

  return (
    <Tooltip
      title={unavailable ? "Assistente indisponível para este perfil" : ""}
    >
      <IconButton
        aria-controls={dockOpen ? "vinx-agent-dock" : undefined}
        aria-expanded={dockOpen}
        aria-label={label}
        onClick={openDock}
      >
        <SmartToyOutlinedIcon />
      </IconButton>
    </Tooltip>
  );
}
