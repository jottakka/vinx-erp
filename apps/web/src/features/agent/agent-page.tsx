import ArrowBackOutlinedIcon from "@mui/icons-material/ArrowBackOutlined";
import { Alert, Button, Card, CardContent, Stack } from "@mui/material";
import { useLocation, useNavigate } from "react-router-dom";
import { PageHeader } from "../../shared/ui/page-header";
import { AgentConversation } from "./agent-conversation";
import { useAgentConversation } from "./agent-conversation-provider";

export function AgentPage() {
  const { access, companyName, openDock } = useAgentConversation();
  const location = useLocation();
  const navigate = useNavigate();
  const description = access.available
    ? "Cada mensagem é uma consulta independente ao agente da empresa ativa; turnos anteriores não fazem parte do contexto."
    : companyName
      ? "Mensagens e anexos permanecem desativados até que seu perfil tenha acesso ao assistente nesta empresa."
      : "Mensagens e anexos permanecem desativados até que uma empresa seja selecionada.";

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
      {access.available ? (
        <Alert role="note" severity="info" variant="outlined">
          Os envios são reais, vinculados à empresa indicada e, quando
          disponível, processados pelo provedor de IA configurado.
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
