import ArrowBackOutlinedIcon from "@mui/icons-material/ArrowBackOutlined";
import { Button, Card, CardContent, Stack } from "@mui/material";
import { useLocation, useNavigate } from "react-router-dom";
import { PageHeader } from "../../shared/ui/page-header";
import { useSession } from "../auth/session-provider";
import { PrototypeNotice } from "../prototypes/prototype-notice";
import { AgentConversation } from "./agent-conversation";
import { useAgentConversation } from "./agent-conversation-provider";

export function AgentPage() {
  const { bootstrap } = useSession();
  const { openDock } = useAgentConversation();
  const location = useLocation();
  const navigate = useNavigate();
  const companyLabel =
    bootstrap?.companies[0]?.name ?? "Nenhuma empresa selecionada";

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
        eyebrow={`${companyLabel} · Página dedicada`}
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
