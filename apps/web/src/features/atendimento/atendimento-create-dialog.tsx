import { zodResolver } from "@hookform/resolvers/zod";
import {
  Alert,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  MenuItem,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { useEffect, useRef, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { CompanySelect } from "../../shared/ui/company-select";
import type { AuthorizedCompanyMembership } from "../auth/session-contract";
import { createAtendimentoAttachmentUploader } from "./atendimento-attachment-uploader";
import type { AtendimentoAssignmentOption } from "./atendimento-client";

export type AtendimentoCreateValues = z.infer<typeof schema>;
const schema = z.object({
  description: z
    .string()
    .trim()
    .min(1, "Informe uma descrição.")
    .max(4000, "A descrição deve ter no máximo 4.000 caracteres."),
  title: z
    .string()
    .trim()
    .min(1, "Informe um título.")
    .max(200, "O título deve ter no máximo 200 caracteres."),
});
type Values = z.output<typeof schema> & {
  attachmentFileId?: string;
  participantMembershipIds: readonly string[];
  requestId: string;
};
export type AtendimentoCreateSubmitResult =
  | { ok: true }
  | { ok: false; message: string };
type Props = {
  assignmentOptions: readonly AtendimentoAssignmentOption[];
  companies?: readonly Pick<
    AuthorizedCompanyMembership,
    "companyId" | "companyName"
  >[];
  companyId: string;
  onClose: () => void;
  onCompanyChange?: (companyId: string) => void;
  onSubmit: (values: Values) => Promise<AtendimentoCreateSubmitResult>;
  open: boolean;
  uploader?: AtendimentoAttachmentUploader;
};
export type AtendimentoAttachmentUploader = ReturnType<
  typeof createAtendimentoAttachmentUploader
>;
const defaultUploader = createAtendimentoAttachmentUploader();

type CreatePayload = {
  attachmentKey: string | null;
  companyId: string;
  description: string;
  participantMembershipIds: readonly string[];
  title: string;
};
type CreateAttempt = {
  attachmentFileId?: string;
  payload: CreatePayload;
  requestId: string;
};

function attachmentKey(file: File | null) {
  return file
    ? `${file.name}\u0000${file.type}\u0000${file.size}\u0000${file.lastModified}`
    : null;
}

function sameCreatePayload(left: CreatePayload, right: CreatePayload) {
  return (
    left.attachmentKey === right.attachmentKey &&
    left.companyId === right.companyId &&
    left.description === right.description &&
    left.title === right.title &&
    left.participantMembershipIds.length ===
      right.participantMembershipIds.length &&
    left.participantMembershipIds.every(
      (membershipId, index) =>
        membershipId === right.participantMembershipIds[index],
    )
  );
}

/** Reúne um anexo confirmado e participantes nomeados para uma empresa explícita. */
export function AtendimentoCreateDialog({
  assignmentOptions,
  companies,
  companyId,
  onClose,
  onCompanyChange,
  onSubmit,
  open,
  uploader: providedUploader,
}: Props) {
  const form = useForm<AtendimentoCreateValues>({
    defaultValues: { description: "", title: "" },
    resolver: zodResolver(schema),
  });
  const uploadRef = useRef<AbortController | null>(null);
  const attachmentInputRef = useRef<HTMLInputElement>(null);
  const epochRef = useRef(0);
  const createAttemptRef = useRef<CreateAttempt | null>(null);
  const [attachment, setAttachment] = useState<File | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [phase, setPhase] = useState<"idle" | "attaching" | "creating">("idle");
  const [participantMembershipIds, setParticipantMembershipIds] = useState<
    readonly string[]
  >([]);
  const titleField = form.register("title");
  const descriptionField = form.register("description");

  function invalidateAttempt() {
    createAttemptRef.current = null;
    setError(null);
  }

  function reset() {
    createAttemptRef.current = null;
    setAttachment(null);
    setError(null);
    setParticipantMembershipIds([]);
    if (attachmentInputRef.current) attachmentInputRef.current.value = "";
    form.reset({ description: "", title: "" });
  }
  function close() {
    if (phase !== "idle") return;
    epochRef.current++;
    uploadRef.current?.abort();
    uploadRef.current = null;
    setPhase("idle");
    reset();
    onClose();
  }
  useEffect(() => () => uploadRef.current?.abort(), []);
  useEffect(() => {
    if (!open) return;
    if (!companyId) return;
    epochRef.current++;
    uploadRef.current?.abort();
    uploadRef.current = null;
    createAttemptRef.current = null;
    setPhase("idle");
    setAttachment(null);
    setError(null);
    setParticipantMembershipIds([]);
    form.reset({ description: "", title: "" });
  }, [companyId, form, open]);
  async function submit(values: AtendimentoCreateValues) {
    if (phase !== "idle") return;
    const payload: CreatePayload = {
      attachmentKey: attachmentKey(attachment),
      companyId,
      description: values.description,
      participantMembershipIds: [...new Set(participantMembershipIds)].sort(),
      title: values.title,
    };
    const currentAttempt = createAttemptRef.current;
    const attempt =
      currentAttempt && sameCreatePayload(currentAttempt.payload, payload)
        ? currentAttempt
        : { payload, requestId: crypto.randomUUID() };
    createAttemptRef.current = attempt;
    setPhase(attachment ? "attaching" : "creating");
    setError(null);
    const epoch = ++epochRef.current;
    let attachmentFileId = attempt.attachmentFileId;
    if (attachment && !attachmentFileId) {
      const controller = new AbortController();
      uploadRef.current = controller;
      const result = await (providedUploader ?? defaultUploader).upload({
        companyId,
        file: attachment,
        signal: controller.signal,
      });
      if (epoch !== epochRef.current) return;
      uploadRef.current = null;
      if (!result.ok) {
        setPhase("idle");
        setError(result.message);
        return;
      }
      attachmentFileId = result.attachmentFileId;
      attempt.attachmentFileId = attachmentFileId;
    }
    if (epoch !== epochRef.current || createAttemptRef.current !== attempt)
      return;
    setPhase("creating");
    const result = await onSubmit({
      attachmentFileId,
      description: payload.description,
      participantMembershipIds: payload.participantMembershipIds,
      requestId: attempt.requestId,
      title: payload.title,
    });
    if (epoch !== epochRef.current || createAttemptRef.current !== attempt)
      return;
    if (!result.ok) {
      setPhase("idle");
      setError(result.message);
      return;
    }
    setPhase("idle");
    reset();
    onClose();
  }
  return (
    <Dialog fullWidth maxWidth="md" open={open} onClose={close}>
      <DialogTitle>Novo atendimento</DialogTitle>
      <DialogContent sx={{ pb: { xs: 1, sm: 2 } }}>
        <Stack
          component="form"
          id="atendimento-create-form"
          onSubmit={form.handleSubmit(submit)}
          spacing={2}
          sx={{ pt: 1, width: "100%" }}
        >
          {error ? <Alert severity="error">{error}</Alert> : null}
          {phase !== "idle" ? (
            <Typography aria-live="polite" role="status">
              {phase === "attaching"
                ? "Anexando arquivo…"
                : "Criando atendimento…"}
            </Typography>
          ) : null}
          <Stack direction={{ md: "row", xs: "column" }} spacing={2}>
            <TextField
              fullWidth
              label="Título"
              disabled={phase !== "idle"}
              error={Boolean(form.formState.errors.title)}
              helperText={form.formState.errors.title?.message}
              slotProps={{ htmlInput: { maxLength: 200 } }}
              {...titleField}
              onChange={(event) => {
                invalidateAttempt();
                titleField.onChange(event);
              }}
            />
            <CompanySelect
              companies={
                companies ?? [{ companyId, companyName: "Empresa selecionada" }]
              }
              disabled={phase !== "idle" || !onCompanyChange}
              label="Empresa do atendimento"
              onChange={(nextCompanyId) => {
                if (nextCompanyId !== companyId)
                  onCompanyChange?.(nextCompanyId);
              }}
              value={companyId}
            />
          </Stack>
          <TextField
            label="Pessoas envolvidas"
            disabled={phase !== "idle"}
            select
            slotProps={{ select: { multiple: true } }}
            value={participantMembershipIds}
            onChange={(event) => {
              invalidateAttempt();
              setParticipantMembershipIds(
                event.target.value as unknown as string[],
              );
            }}
          >
            {assignmentOptions.map((option) => (
              <MenuItem key={option.membershipId} value={option.membershipId}>
                {option.displayName}
              </MenuItem>
            ))}
          </TextField>
          <Button
            disabled={phase !== "idle"}
            onClick={() => attachmentInputRef.current?.click()}
            variant="outlined"
          >
            {attachment ? attachment.name : "Anexar arquivo (opcional)"}
          </Button>
          <input
            accept="application/pdf,image/jpeg,image/png"
            aria-label="Selecionar anexo do atendimento"
            data-testid="atendimento-attachment-picker"
            hidden
            onChange={(event) => {
              invalidateAttempt();
              setAttachment(event.target.files?.[0] ?? null);
              event.target.value = "";
            }}
            ref={attachmentInputRef}
            type="file"
          />
          <TextField
            label="Descrição"
            disabled={phase !== "idle"}
            error={Boolean(form.formState.errors.description)}
            helperText={form.formState.errors.description?.message}
            minRows={3}
            multiline
            slotProps={{ htmlInput: { maxLength: 4000 } }}
            {...descriptionField}
            onChange={(event) => {
              invalidateAttempt();
              descriptionField.onChange(event);
            }}
          />
        </Stack>
      </DialogContent>
      <DialogActions
        sx={{
          "& > .MuiButton-root": { width: { sm: "auto", xs: "100%" } },
          flexDirection: { sm: "row", xs: "column-reverse" },
          gap: { sm: 0, xs: 1 },
          px: { sm: 3, xs: 2 },
        }}
      >
        <Button disabled={phase !== "idle"} onClick={close}>
          Cancelar
        </Button>
        <Button
          disabled={phase !== "idle"}
          form="atendimento-create-form"
          type="submit"
          variant="contained"
        >
          {phase === "attaching"
            ? "Anexando…"
            : phase === "creating"
              ? "Criando…"
              : "Criar cartão"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
