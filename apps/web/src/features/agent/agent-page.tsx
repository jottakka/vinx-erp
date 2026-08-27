import ArrowBackOutlinedIcon from "@mui/icons-material/ArrowBackOutlined";
import {
  Alert,
  Button,
  Card,
  CardContent,
  MenuItem,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { isCanonicalUuid } from "../../shared/http/bff-response";
import { PageHeader } from "../../shared/ui/page-header";
import { useSession } from "../auth/session-provider";
import { AgentConversation } from "./agent-conversation";
import { useAgentConversation } from "./agent-conversation-provider";

export function AgentPage() {
  const { access, companyName, openDock } = useAgentConversation();
  const location = useLocation();
  const navigate = useNavigate();
  const channelSelection = new URLSearchParams(location.search).get(
    "channelSelection",
  );
  const hasChannelSelection =
    channelSelection !== null && isCanonicalUuid(channelSelection);
  const description = access.available
    ? "Envie perguntas sobre a empresa selecionada."
    : companyName
      ? "Escolha outra empresa para continuar."
      : "Escolha uma empresa antes de enviar uma pergunta.";
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
        description={
          hasChannelSelection
            ? "A escolha vale para novas mensagens recebidas pelo canal."
            : description
        }
        eyebrow="Assistente"
        title={
          hasChannelSelection ? "Escolha a empresa para o Telegram" : "Agente"
        }
      />
      {hasChannelSelection ? (
        <TelegramCompanySelection inboundMessageId={channelSelection} />
      ) : null}
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
            {hasChannelSelection ? null : <AgentConversation />}
          </Stack>
        </CardContent>
      </Card>
    </Stack>
  );
}

function TelegramCompanySelection({
  inboundMessageId,
}: {
  inboundMessageId: string;
}) {
  const { bootstrap } = useSession();
  const [companyId, setCompanyId] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [result, setResult] = useState<"idle" | "saved" | "failed">("idle");

  async function selectCompany() {
    if (!companyId || submitting) return;
    setSubmitting(true);
    setResult("idle");
    try {
      const tokenResponse = await fetch(
        "/foundation/identity/browser-session/antiforgery",
        {
          cache: "no-store",
          credentials: "same-origin",
          headers: { Accept: "application/json" },
          method: "GET",
          mode: "same-origin",
          redirect: "error",
        },
      );
      const tokenBody = (await tokenResponse.json()) as {
        requestToken?: unknown;
      };
      if (!tokenResponse.ok || typeof tokenBody.requestToken !== "string") {
        setResult("failed");
        return;
      }

      const response = await fetch("/agent/channel-company-selection", {
        body: JSON.stringify({
          companyId,
          inboundMessageId,
          requestId: crypto.randomUUID(),
        }),
        cache: "no-store",
        credentials: "same-origin",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": tokenBody.requestToken,
        },
        method: "POST",
        mode: "same-origin",
        redirect: "error",
      });
      setResult(response.ok ? "saved" : "failed");
    } catch {
      setResult("failed");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Card variant="outlined">
      <CardContent>
        <Stack spacing={2}>
          <Typography variant="body2">
            Confirme uma empresa ativa. A mensagem que abriu este link não será
            processada; envie uma nova mensagem no Telegram depois de salvar.
          </Typography>
          <TextField
            disabled={submitting || result === "saved"}
            label="Empresa do Telegram"
            onChange={(event) => setCompanyId(event.target.value)}
            select
            value={companyId}
          >
            {bootstrap?.companies.map((company) => (
              <MenuItem key={company.membershipId} value={company.companyId}>
                {company.companyName}
              </MenuItem>
            ))}
          </TextField>
          <Button
            disabled={!companyId || submitting || result === "saved"}
            onClick={() => void selectCompany()}
            sx={{ alignSelf: "flex-start" }}
            variant="contained"
          >
            Salvar empresa
          </Button>
          {result === "saved" ? (
            <Alert severity="success">
              Preferência salva. A próxima mensagem usará essa empresa.
            </Alert>
          ) : null}
          {result === "failed" ? (
            <Alert severity="error">
              Não foi possível salvar. Abra novamente o link recebido no
              Telegram.
            </Alert>
          ) : null}
        </Stack>
      </CardContent>
    </Card>
  );
}
