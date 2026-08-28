import SmartToyOutlinedIcon from "@mui/icons-material/SmartToyOutlined";
import { IconButton } from "@mui/material";
import { useAgentConversation } from "./agent-conversation-provider";

export function AgentTrigger() {
  const { dockOpen, openDock } = useAgentConversation();

  return (
    <IconButton
      aria-controls={dockOpen ? "vinx-agent-dock" : undefined}
      aria-expanded={dockOpen}
      aria-label="Abrir painel do agente"
      onClick={openDock}
    >
      <SmartToyOutlinedIcon />
    </IconButton>
  );
}
