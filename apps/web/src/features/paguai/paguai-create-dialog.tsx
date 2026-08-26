import { zodResolver } from "@hookform/resolvers/zod";
import {
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  MenuItem,
  Stack,
  TextField,
} from "@mui/material";
import { useState } from "react";
import { Controller, useForm } from "react-hook-form";
import { z } from "zod";
import {
  AttachmentField,
  type AttachmentFieldValue,
  type AttachmentUploader,
} from "../../shared/ui/attachment-field";
import { amountFromReais } from "./paguai-format";

export type PaguaiCreateValues = z.infer<typeof paguaiCreateSchema>;

const paguaiCreateSchema = z.object({
  amountReais: z.string().trim().min(1, "Informe um valor."),
  beneficiaryDisplayName: z
    .string()
    .trim()
    .min(1, "Informe o beneficiário.")
    .max(200),
  description: z.string().trim().min(1, "Informe uma descrição.").max(4000),
  dueDate: z.string().min(1, "Informe o vencimento."),
  proofRequirement: z.enum(["optional", "required"], {
    message: "Selecione o comprovante.",
  }),
  title: z.string().trim().min(1, "Informe um título.").max(200),
});

type PaguaiFormValues = z.output<typeof paguaiCreateSchema>;

type PaguaiCreateDialogProps = {
  onClose: () => void;
  onSubmit: (values: {
    amount: string;
    beneficiaryDisplayName: string;
    description: string;
    dueDate: string;
    proofRequirement: "optional" | "required";
    proofFileId: string | null;
    title: string;
  }) => void;
  open: boolean;
  uploader?: AttachmentUploader;
};

export function PaguaiCreateDialog({
  onClose,
  onSubmit,
  open,
  uploader,
}: PaguaiCreateDialogProps) {
  const form = useForm<PaguaiFormValues>({
    defaultValues: {
      amountReais: "",
      beneficiaryDisplayName: "",
      description: "",
      dueDate: "",
      proofRequirement: "" as PaguaiFormValues["proofRequirement"],
      title: "",
    },
    resolver: zodResolver(paguaiCreateSchema),
  });
  const [attachments, setAttachments] = useState<
    readonly AttachmentFieldValue[]
  >([]);
  const [amountError, setAmountError] = useState<string | null>(null);

  function close() {
    form.reset();
    setAttachments([]);
    setAmountError(null);
    onClose();
  }

  function submit(values: PaguaiFormValues) {
    const amount = amountFromReais(values.amountReais);
    if (amount === null) {
      setAmountError("Informe um valor válido em reais (ex.: 1.250,00).");
      return;
    }
    const readyProof = attachments.find((item) => item.state === "ready");
    onSubmit({
      amount,
      beneficiaryDisplayName: values.beneficiaryDisplayName,
      description: values.description,
      dueDate: values.dueDate,
      proofFileId: readyProof?.fileId ?? null,
      proofRequirement: values.proofRequirement,
      title: values.title,
    });
  }

  return (
    <Dialog fullWidth maxWidth="sm" onClose={close} open={open}>
      <DialogTitle>Novo rascunho</DialogTitle>
      <DialogContent>
        <Stack
          component="form"
          id="paguai-create-form"
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
          <TextField
            error={
              Boolean(form.formState.errors.amountReais) || Boolean(amountError)
            }
            helperText={
              amountError ?? form.formState.errors.amountReais?.message
            }
            inputMode="decimal"
            label="Valor (R$)"
            placeholder="1.250,00"
            sx={{ minWidth: 0 }}
            {...form.register("amountReais")}
          />
          <TextField
            error={Boolean(form.formState.errors.dueDate)}
            helperText={form.formState.errors.dueDate?.message}
            label="Vencimento"
            slotProps={{ htmlInput: { min: "0001-01-01", type: "date" } }}
            {...form.register("dueDate")}
          />
          <TextField
            error={Boolean(form.formState.errors.beneficiaryDisplayName)}
            helperText={form.formState.errors.beneficiaryDisplayName?.message}
            label="Beneficiário (informativo)"
            slotProps={{ htmlInput: { maxLength: 200 } }}
            {...form.register("beneficiaryDisplayName")}
          />
          <Controller
            control={form.control}
            name="proofRequirement"
            render={({ field: { ref, ...field }, fieldState }) => (
              <TextField
                error={Boolean(fieldState.error)}
                helperText={fieldState.error?.message}
                inputRef={ref}
                label="Comprovante"
                select
                value={field.value ?? ""}
                onChange={field.onChange}
                onBlur={field.onBlur}
              >
                <MenuItem value="">Selecione</MenuItem>
                <MenuItem value="required">Obrigatório</MenuItem>
                <MenuItem value="optional">Opcional</MenuItem>
              </TextField>
            )}
          />
          <AttachmentField
            accept="application/pdf,image/jpeg,image/png"
            label="Anexar comprovante"
            onChange={setAttachments}
            uploader={
              uploader ??
              (async () => ({
                ok: false as const,
                error: {
                  kind: "validation" as const,
                  message: "Anexo indisponível.",
                },
              }))
            }
            value={attachments}
          />
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
        <Button form="paguai-create-form" type="submit" variant="contained">
          Criar rascunho
        </Button>
      </DialogActions>
    </Dialog>
  );
}
