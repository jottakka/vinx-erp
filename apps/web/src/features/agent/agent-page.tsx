import ArrowBackOutlinedIcon from "@mui/icons-material/ArrowBackOutlined";
import { Alert, Button, Card, CardContent, Stack } from "@mui/material";
import { useLocation, useNavigate } from "react-router-dom";
import { PageHeader } from "../../shared/ui/page-header";
import { AgentConversation } from "./agent-conversation";
import { useAgentConversation } from "./agent-conversation-provider";

export function AgentPage() {
  const { companyName, openDock } = useAgentConversation();
  const location = useLocation();
  const navigate = useNavigate();
  const description = companyName
    ? "Converse com o agente no contexto da empresa selecionada."
    : "Selecione uma empresa para conversar com o agente; turnos anteriores não fazem parte do contexto.";
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
        description={description}
        eyebrow={`${companyName ?? "Sem empresa"} · Página dedicada`}
        title="Agente"
      />
      {companyName ? (
        <Alert role="note" severity="info" variant="outlined">
          O agente usa somente dados autorizados da empresa selecionada.
        </Alert>
      ) : null}
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
