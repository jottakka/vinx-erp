import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { type FormEvent, useState } from "react";
import type { AgentMessage } from "./agent-contract";
import { useAgentConversation } from "./agent-conversation-provider";

function MessageBubble({ message }: { message: AgentMessage }) {
  const isUser = message.role === "user";

  return (
    <Stack
      sx={{
        alignItems: isUser ? "flex-end" : "flex-start",
        maxWidth: "85%",
      }}
    >
      <Typography
        sx={{
          bgcolor: isUser ? "primary.main" : "background.paper",
          border: 1,
          borderColor: isUser ? "primary.main" : "divider",
          borderRadius: 12,
          color: isUser ? "primary.contrastText" : "text.primary",
          px: 1.5,
          py: 1,
        }}
      >
        {message.text}
      </Typography>
      {message.sources ? (
        <Stack direction="row" spacing={0.5} sx={{ flexWrap: "wrap", mt: 0.5 }}>
          {message.sources.map((source) => (
            <Chip
              key={source.label}
              label={source.label}
              size="small"
              variant="outlined"
            />
          ))}
        </Stack>
      ) : null}
    </Stack>
  );
}

export function AgentConversation() {
  const { companyName, messages, sendMessage, status } = useAgentConversation();
  const [draft, setDraft] = useState("");
  const thinking = status === "thinking";
  const emptyConversationNotice = companyName
    ? "Cada envio é uma consulta independente ao agente da empresa ativa; turnos anteriores não são relembrados."
    : "Nenhuma empresa foi selecionada. O servidor solicitará contexto antes de processar o envio.";

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    sendMessage(draft);
    setDraft("");
  }

  return (
    <Stack sx={{ flexGrow: 1, minHeight: 0 }}>
      <Box
        aria-label="Mensagens do agente"
        role="log"
        sx={{
          "& > *": { mb: 1.5 },
          flexGrow: 1,
          overflowY: "auto",
          py: 0.5,
        }}
      >
        {messages.length ? (
          messages.map((message) => (
            <MessageBubble key={message.id} message={message} />
          ))
        ) : (
          <Alert role="note" severity="info">
            {emptyConversationNotice}
          </Alert>
        )}
        {thinking ? (
          <Stack direction="row" spacing={1} sx={{ alignItems: "center" }}>
            <CircularProgress aria-hidden size={16} />
            <Typography color="text.secondary">Analisando…</Typography>
          </Stack>
        ) : null}
      </Box>
      <Stack component="form" direction="row" onSubmit={submit} spacing={1}>
        <TextField
          fullWidth
          label="Mensagem para o agente"
          maxRows={3}
          multiline
          onChange={(event) => setDraft(event.target.value)}
          size="small"
          value={draft}
        />
        <Button disabled={!draft.trim() || thinking} type="submit">
          Enviar
        </Button>
      </Stack>
    </Stack>
  );
}
