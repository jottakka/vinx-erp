import ArrowBackOutlinedIcon from "@mui/icons-material/ArrowBackOutlined";
import { Button, Card, CardContent, Stack } from "@mui/material";
import { useLocation, useNavigate } from "react-router-dom";
import { PageHeader } from "../../shared/ui/page-header";
import { PrototypeNotice } from "../prototypes/prototype-notice";
import { AgentConversation } from "./agent-conversation";
import { useAgentConversation } from "./agent-conversation-provider";

export function AgentPage() {
  const { openDock } = useAgentConversation();
  const location = useLocation();
  const navigate = useNavigate();

  function backToContext() {
    openDock();

    if (location.key === "default") {
      navigate("/");
      return;
    }

    navigate(-1);
  }

  return (
    <Stack spacing={3}>
      <PageHeader
        description="Conversa compartilhada com o painel do agente, avaliada apenas em demonstração local."
        eyebrow="Todas as empresas autorizadas · Página dedicada"
        title="Agente"
      />
      <PrototypeNotice />
      <Card variant="outlined">
        <CardContent>
          <Stack sx={{ gap: 2, minHeight: { md: "60dvh" } }}>
            <Button
              onClick={backToContext}
              size="small"
              startIcon={<ArrowBackOutlinedIcon />}
              sx={{ alignSelf: "flex-start" }}
            >
              Voltar ao painel do agente
            </Button>
            <AgentConversation />
          </Stack>
        </CardContent>
      </Card>
    </Stack>
  );
}
