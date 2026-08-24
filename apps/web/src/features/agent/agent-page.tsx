import ArrowBackOutlinedIcon from "@mui/icons-material/ArrowBackOutlined";
import { Button, Card, CardContent, Stack } from "@mui/material";
import { useLocation, useNavigate } from "react-router-dom";
import { PageHeader } from "../../shared/ui/page-header";
import { PrototypeNotice } from "../prototypes/prototype-notice";
import { AgentConversation } from "./agent-conversation";
import { useAgentConversation } from "./agent-conversation-provider";

export function AgentPage() {
  const { companyName, openDock } = useAgentConversation();
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
        description="Cada mensagem é uma consulta independente ao agente da empresa ativa; turnos anteriores não fazem parte do contexto."
        eyebrow={`${companyName ?? "Sem empresa"} · Página dedicada`}
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
