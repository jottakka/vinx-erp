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
import { useState } from "react";
import { Controller, useForm } from "react-hook-form";
import { z } from "zod";
import {
  AttachmentField,
  type AttachmentFieldValue,
  type AttachmentUploader,
} from "../../shared/ui/attachment-field";
import { CompanyIdentity } from "../../shared/ui/company-identity";
import { amountFromReais, isValidDateOnly } from "./paguai-format";

export type PaguaiCreateValues = z.infer<typeof paguaiCreateSchema>;

const paguaiCreateSchema = z.object({
  amountReais: z.string().trim().min(1, "Informe um valor."),
  beneficiaryDisplayName: z
    .string()
    .trim()
    .min(1, "Informe o beneficiário.")
    .max(200),
  description: z.string().trim().min(1, "Informe uma descrição.").max(4000),
  dueDate: z
    .string()
    .min(1, "Informe o vencimento.")
    .refine((value) => isValidDateOnly(value), "Informe uma data válida."),
  proofRequirement: z.enum(["optional", "required"], {
    message: "Selecione o comprovante.",
  }),
  title: z.string().trim().min(1, "Informe um título.").max(200),
});

type PaguaiFormValues = z.output<typeof paguaiCreateSchema>;

type PaguaiCreateDialogProps = {
  company: { companyId: string; companyName: string } | null;
  onClose: () => void;
  onSubmit: (values: {
    amount: string;
    beneficiaryDisplayName: string;
    description: string;
    dueDate: string;
    proofRequirement: "optional" | "required";
    invoiceFileId: string | null;
    title: string;
  }) => void;
  open: boolean;
  uploader?: AttachmentUploader;
  errorMessage?: string | null;
  submitting?: boolean;
};

export function PaguaiCreateDialog({
  company,
  errorMessage,
  onClose,
  onSubmit,
  open,
  submitting = false,
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
  const isUploading = attachments.some((item) => item.state === "uploading");

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
    const readyInvoice = attachments.find((item) => item.state === "ready");
    onSubmit({
      amount,
      beneficiaryDisplayName: values.beneficiaryDisplayName,
      description: values.description,
      dueDate: values.dueDate,
      invoiceFileId: readyInvoice?.fileId ?? null,
      proofRequirement: values.proofRequirement,
      title: values.title,
    });
  }

  return (
    <Dialog
      fullWidth
      maxWidth="sm"
      onClose={(_event, _reason) => {
        if (!submitting) close();
      }}
      open={open}
    >
      <DialogTitle>Nova solicitação</DialogTitle>
      {company ? (
        <Stack sx={{ px: 3, pb: 1 }}>
          <CompanyIdentity
            companyId={company.companyId}
            companyName={company.companyName}
          />
        </Stack>
      ) : null}
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
            label="Pagar até"
            slotProps={{
              htmlInput: { min: "0001-01-01", type: "date" },
              inputLabel: { shrink: true },
            }}
            {...form.register("dueDate")}
          />
          <TextField
            error={Boolean(form.formState.errors.beneficiaryDisplayName)}
            helperText={
              form.formState.errors.beneficiaryDisplayName?.message ??
              "Nome para referência; não confirma dados bancários."
            }
            label="Quem receberá"
            slotProps={{ htmlInput: { maxLength: 200 } }}
            {...form.register("beneficiaryDisplayName")}
          />
          <Controller
            control={form.control}
            name="proofRequirement"
            render={({ field: { ref, ...field }, fieldState }) => (
              <TextField
                error={Boolean(fieldState.error)}
                helperText={
                  fieldState.error?.message ??
                  "Será exigido na confirmação do pagamento."
                }
                inputRef={ref}
                label="Comprovante de pagamento"
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
            label="Fatura ou documento da solicitação"
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
          {errorMessage ? (
            <Alert role="alert" severity="error">
              {errorMessage}
            </Alert>
          ) : null}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button disabled={submitting} onClick={close}>
          Cancelar
        </Button>
        <Button
          disabled={isUploading || submitting}
          form="paguai-create-form"
          type="submit"
          variant="contained"
        >
          {isUploading
            ? "Anexando…"
            : submitting
              ? "Criando…"
              : "Criar rascunho"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
