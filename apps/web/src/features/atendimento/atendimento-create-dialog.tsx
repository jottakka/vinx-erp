import { zodResolver } from "@hookform/resolvers/zod";
import {
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  TextField,
} from "@mui/material";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { createAtendimentoAttachmentUploader } from "./atendimento-attachment-uploader";

export type AtendimentoCreateValues = z.infer<typeof atendimentoCreateSchema>;

const atendimentoCreateSchema = z.object({
  description: z.string().trim().min(1, "Informe uma descrição.").max(4000),
  title: z.string().trim().min(1, "Informe um título.").max(200),
});

type AtendimentoValues = z.output<typeof atendimentoCreateSchema> & {
  attachmentFileId?: string;
};

type AtendimentoCreateDialogProps = {
  onClose: () => void;
  onSubmit: (values: AtendimentoValues) => void;
  open: boolean;
  companyId: string;
};

const attachmentUploader = createAtendimentoAttachmentUploader();

/**
 * Modal de criação de cartão do Atendimento/Kanban. Substitui o formulário
 * inline; a validação local passa pelo backend na operação real.
 */
export function AtendimentoCreateDialog({
  onClose,
  onSubmit,
  open,
  companyId,
}: AtendimentoCreateDialogProps) {
  const form = useForm<AtendimentoValues>({
    defaultValues: { description: "", title: "" },
    resolver: zodResolver(atendimentoCreateSchema),
  });
  const [attachment, setAttachment] = useState<File | null>(null);
  const [attachmentError, setAttachmentError] = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);

  function close() {
    setAttachment(null);
    setAttachmentError(null);
    form.reset();
    onClose();
  }

  async function submit(values: AtendimentoValues) {
    let attachmentFileId: string | undefined;
    if (attachment) {
      setUploading(true);
      setAttachmentError(null);
      const result = await attachmentUploader.upload({
        companyId,
        file: attachment,
        signal: new AbortController().signal,
      });
      setUploading(false);
      if (!result.ok) return setAttachmentError(result.message);
      attachmentFileId = result.attachmentFileId;
    }
    onSubmit({ ...values, attachmentFileId });
    close();
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
          <TextField
            error={Boolean(form.formState.errors.title)}
            helperText={form.formState.errors.title?.message}
            label="Título"
            slotProps={{ htmlInput: { maxLength: 200 } }}
            {...form.register("title")}
          />
          <Button component="label" variant="outlined">
            {attachment ? attachment.name : "Anexar arquivo (opcional)"}
            <input
              accept="application/pdf,image/jpeg,image/png"
              data-testid="atendimento-attachment-picker"
              hidden
              onChange={(event) => {
                setAttachment(event.target.files?.[0] ?? null);
                setAttachmentError(null);
              }}
              type="file"
            />
          </Button>
          {attachmentError ? <span role="alert">{attachmentError}</span> : null}
          <TextField
            error={Boolean(form.formState.errors.description)}
            helperText={form.formState.errors.description?.message}
            label="Descrição"
            minRows={3}
            multiline
            slotProps={{ htmlInput: { maxLength: 4000 } }}
            {...form.register("description")}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>Cancelar</Button>
        <Button
          form="atendimento-create-form"
          type="submit"
          variant="contained"
        >
          {uploading ? "Enviando anexo…" : "Criar cartão"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
