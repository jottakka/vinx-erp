import AttachFileOutlinedIcon from "@mui/icons-material/AttachFileOutlined";
import SendOutlinedIcon from "@mui/icons-material/SendOutlined";
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  IconButton,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import {
  type ChangeEvent,
  type FormEvent,
  type KeyboardEvent,
  useState,
} from "react";
import { CompanySelect } from "../../shared/ui/company-select";
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

function attachmentStateText(state: "uploading" | "ready" | "failed") {
  switch (state) {
    case "uploading":
      return "Enviando";
    case "ready":
      return "Pronto";
    case "failed":
      return "Falhou";
  }
}

function attachmentFailureText(
  stage: "validation" | "intention" | "storage" | "confirmation" | undefined,
) {
  switch (stage) {
    case "intention":
      return "Não foi possível preparar o anexo.";
    case "storage":
      return "Não foi possível enviar o anexo.";
    case "confirmation":
      return "Não foi possível confirmar o anexo.";
    default:
      return "O anexo não atende aos requisitos permitidos.";
  }
}

export function AgentConversation() {
  const {
    attachments,
    authorizedCompanies,
    attachmentSelectionAvailable,
    attachmentSelectionLimitReached,
    companyName,
    selectCompany,
    selectedCompanyId,
    messages,
    removeAttachment,
    selectAttachments,
    sendMessage,
    status,
  } = useAgentConversation();
  const [draft, setDraft] = useState("");
  const thinking = status === "thinking";
  const emptyConversationNotice = companyName
    ? "Faça uma pergunta para começar."
    : "Escolha uma empresa para começar.";

  function submitFromInput() {
    sendMessage(draft);
    setDraft("");
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    submitFromInput();
  }

  function handleComposerKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    // Enter envia; Shift+Enter insere uma nova linha. Não envia durante a
    // composição de IME (ex.: CJK) para não disparar um envio prematuro.
    if (
      event.key === "Enter" &&
      !event.shiftKey &&
      !event.nativeEvent.isComposing
    ) {
      event.preventDefault();
      submitFromInput();
    }
  }

  function selectFiles(event: ChangeEvent<HTMLInputElement>) {
    selectAttachments(Array.from(event.target.files ?? []));
    event.target.value = "";
  }

  const hasBlockingAttachment = attachments.some(
    (attachment) => attachment.state !== "ready",
  );

  return (
    <Stack sx={{ flexGrow: 1, minHeight: 0 }}>
      <Stack spacing={1} sx={{ mb: 2 }}>
        <CompanySelect
          companies={authorizedCompanies}
          label="Empresa do envio"
          onChange={selectCompany}
          value={selectedCompanyId}
        />
        {companyName ? (
          <Typography color="text.secondary" variant="caption">
            Uma empresa por envio.
          </Typography>
        ) : null}
      </Stack>
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
      <Stack component="form" onSubmit={submit} spacing={1}>
        {companyName && !attachmentSelectionAvailable ? (
          <Alert severity="info">
            Selecione uma empresa para anexar documentos ao agente.
          </Alert>
        ) : null}
        {attachmentSelectionLimitReached ? (
          <Alert severity="error">
            Você pode selecionar no máximo três anexos.
          </Alert>
        ) : null}
        {attachments.length ? (
          <Stack
            aria-label="Anexos selecionados"
            component="ul"
            spacing={0.5}
            sx={{ listStyle: "none", m: 0, p: 0 }}
          >
            {attachments.map((attachment) => (
              <Stack component="li" key={attachment.localId} spacing={0.25}>
                <Stack
                  direction="row"
                  spacing={0.5}
                  sx={{ alignItems: "center" }}
                >
                  <Typography sx={{ flexGrow: 1 }} variant="body2">
                    {attachment.fileName}
                  </Typography>
                  <Chip
                    label={attachmentStateText(attachment.state)}
                    size="small"
                  />
                  <IconButton
                    aria-label={`Remover ${attachment.fileName}`}
                    onClick={() => removeAttachment(attachment.localId)}
                    size="small"
                  >
                    ×
                  </IconButton>
                </Stack>
                {attachment.state === "failed" ? (
                  <Typography color="error" variant="caption">
                    {attachmentFailureText(attachment.errorStage)}
                  </Typography>
                ) : null}
              </Stack>
            ))}
          </Stack>
        ) : null}
        <Stack direction="row" spacing={1} sx={{ alignItems: "flex-end" }}>
          <TextField
            disabled={!selectedCompanyId || thinking}
            fullWidth
            label="Mensagem para o agente"
            maxRows={3}
            multiline
            onChange={(event) => setDraft(event.target.value)}
            onKeyDown={handleComposerKeyDown}
            size="small"
            value={draft}
          />
          <IconButton
            aria-label="Anexar arquivo"
            component="label"
            disabled={thinking || !attachmentSelectionAvailable}
            title="Anexar arquivo"
          >
            <AttachFileOutlinedIcon />
            <input
              accept="application/pdf,image/jpeg,image/png"
              aria-label="Anexar documentos ao agente"
              disabled={thinking || !attachmentSelectionAvailable}
              multiple
              onChange={selectFiles}
              style={{ display: "none" }}
              type="file"
            />
          </IconButton>
          <Button
            disabled={
              !selectedCompanyId ||
              !draft.trim() ||
              thinking ||
              hasBlockingAttachment
            }
            startIcon={<SendOutlinedIcon />}
            type="submit"
          >
            Enviar
          </Button>
        </Stack>
      </Stack>
    </Stack>
  );
}
