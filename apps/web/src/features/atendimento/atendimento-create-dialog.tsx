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
} from "@mui/material";
import { useEffect, useRef, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { createAtendimentoAttachmentUploader } from "./atendimento-attachment-uploader";
import type { AtendimentoAssignmentOption } from "./atendimento-client";

export type AtendimentoCreateValues = z.infer<typeof schema>;
const schema = z.object({
  description: z.string().trim().min(1, "Informe uma descrição.").max(4000),
  title: z.string().trim().min(1, "Informe um título.").max(200),
});
type Values = z.output<typeof schema> & {
  attachmentFileId?: string;
  participantMembershipIds: readonly string[];
};
type Props = {
  assignmentOptions: readonly AtendimentoAssignmentOption[];
  companyId: string;
  onClose: () => void;
  onSubmit: (values: Values) => Promise<void>;
  open: boolean;
};
const uploader = createAtendimentoAttachmentUploader();

/** Collects one confirmed attachment and named participants for one explicit company. */
export function AtendimentoCreateDialog({
  assignmentOptions,
  companyId,
  onClose,
  onSubmit,
  open,
}: Props) {
  const form = useForm<AtendimentoCreateValues>({
    defaultValues: { description: "", title: "" },
    resolver: zodResolver(schema),
  });
  const uploadRef = useRef<AbortController | null>(null);
  const epochRef = useRef(0);
  const [attachment, setAttachment] = useState<File | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [committing, setCommitting] = useState(false);
  const [participantMembershipIds, setParticipantMembershipIds] = useState<
    readonly string[]
  >([]);
  function reset() {
    setAttachment(null);
    setError(null);
    setParticipantMembershipIds([]);
    form.reset({ description: "", title: "" });
  }
  function close() {
    if (committing) return;
    epochRef.current++;
    uploadRef.current?.abort();
    uploadRef.current = null;
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
    setAttachment(null);
    setError(null);
    setParticipantMembershipIds([]);
    form.reset({ description: "", title: "" });
  }, [companyId, form, open]);
  async function submit(values: AtendimentoCreateValues) {
    if (committing) return;
    setCommitting(true);
    setError(null);
    const epoch = ++epochRef.current;
    let attachmentFileId: string | undefined;
    if (attachment) {
      const controller = new AbortController();
      uploadRef.current = controller;
      const result = await uploader.upload({
        companyId,
        file: attachment,
        signal: controller.signal,
      });
      if (epoch !== epochRef.current) return;
      uploadRef.current = null;
      if (!result.ok) {
        setCommitting(false);
        setError(result.message);
        return;
      }
      attachmentFileId = result.attachmentFileId;
    }
    await onSubmit({
      ...values,
      attachmentFileId,
      participantMembershipIds: [...new Set(participantMembershipIds)].sort(),
    });
    if (epoch !== epochRef.current) return;
    setCommitting(false);
    reset();
    onClose();
  }
  return (
    <Dialog open={open} onClose={close}>
      <DialogTitle>Novo atendimento</DialogTitle>
      <DialogContent>
        <Stack
          component="form"
          id="atendimento-create-form"
          onSubmit={form.handleSubmit(submit)}
          spacing={2}
          sx={{ pt: 1 }}
        >
          {error ? <Alert severity="error">{error}</Alert> : null}
          <TextField
            label="Título"
            disabled={committing}
            error={Boolean(form.formState.errors.title)}
            helperText={form.formState.errors.title?.message}
            slotProps={{ htmlInput: { maxLength: 200 } }}
            {...form.register("title")}
          />
          <TextField
            label="Pessoas envolvidas"
            disabled={committing}
            select
            slotProps={{ select: { multiple: true } }}
            value={participantMembershipIds}
            onChange={(event) =>
              setParticipantMembershipIds(
                event.target.value as unknown as string[],
              )
            }
          >
            {assignmentOptions.map((option) => (
              <MenuItem key={option.membershipId} value={option.membershipId}>
                {option.displayName}
              </MenuItem>
            ))}
          </TextField>
          <Button component="label" disabled={committing} variant="outlined">
            {attachment ? attachment.name : "Anexar arquivo (opcional)"}
            <input
              accept="application/pdf,image/jpeg,image/png"
              data-testid="atendimento-attachment-picker"
              hidden
              onChange={(event) =>
                setAttachment(event.target.files?.[0] ?? null)
              }
              type="file"
            />
          </Button>
          <TextField
            label="Descrição"
            disabled={committing}
            error={Boolean(form.formState.errors.description)}
            helperText={form.formState.errors.description?.message}
            minRows={3}
            multiline
            slotProps={{ htmlInput: { maxLength: 4000 } }}
            {...form.register("description")}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button disabled={committing} onClick={close}>
          Cancelar
        </Button>
        <Button
          disabled={committing}
          form="atendimento-create-form"
          type="submit"
          variant="contained"
        >
          {committing ? "Criando…" : "Criar cartão"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
