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
    .max(200, "O nome do beneficiário deve ter no máximo 200 caracteres."),
  description: z
    .string()
    .trim()
    .min(1, "Informe uma descrição.")
    .max(4000, "A descrição deve ter no máximo 4.000 caracteres."),
  dueDate: z
    .string()
    .min(1, "Informe o vencimento.")
    .refine((value) => isValidDateOnly(value), "Informe uma data válida."),
  proofRequirement: z.enum(["optional", "required"], {
    message: "Selecione o comprovante.",
  }),
  title: z
    .string()
    .trim()
    .min(1, "Informe um título.")
    .max(200, "O título deve ter no máximo 200 caracteres."),
});

type PaguaiFormValues = z.output<typeof paguaiCreateSchema>;

type PaguaiCreateDialogProps = {
  company: { companyId: string; companyName: string } | null;
  onClose: () => void;
  onRetry?: () => void;
  onSubmit: (values: {
    amount: string;
    beneficiaryDisplayName: string;
    description: string;
    dueDate: string;
    proofFileId: string | null;
    proofRequirement: "optional" | "required";
    invoiceFileId: string | null;
    title: string;
  }) => void;
  open: boolean;
  uploader?: AttachmentUploader;
  proofUploader?: AttachmentUploader;
  errorMessage?: string | null;
  recoveryRequired?: boolean;
  submitting?: boolean;
};

export function PaguaiCreateDialog({
  company,
  errorMessage,
  onClose,
  onRetry,
  onSubmit,
  open,
  proofUploader,
  recoveryRequired = false,
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
  const [proofAttachments, setProofAttachments] = useState<
    readonly AttachmentFieldValue[]
  >([]);
  const [amountError, setAmountError] = useState<string | null>(null);
  const allAttachments = [...attachments, ...proofAttachments];
  const isUploading = allAttachments.some((item) => item.state === "uploading");
  const hasFailedAttachment = allAttachments.some(
    (item) => item.state === "failed",
  );
  const failedAttachmentMessage = hasFailedAttachment
    ? "O anexo selecionado não foi enviado. Remova-o e selecione o arquivo novamente antes de criar a solicitação."
    : null;
  const locked = submitting || recoveryRequired;

  function close() {
    if (locked) return;
    form.reset();
    setAttachments([]);
    setProofAttachments([]);
    setAmountError(null);
    onClose();
  }

  function submit(values: PaguaiFormValues) {
    if (isUploading || hasFailedAttachment || locked) return;
    const amount = amountFromReais(values.amountReais);
    if (amount === null) {
      setAmountError("Informe um valor válido em reais (ex.: 1.250,00).");
      return;
    }
    setAmountError(null);
    const readyInvoice = attachments.find((item) => item.state === "ready");
    const readyProof = proofAttachments.find((item) => item.state === "ready");
    onSubmit({
      amount,
      beneficiaryDisplayName: values.beneficiaryDisplayName,
      description: values.description,
      dueDate: values.dueDate,
      invoiceFileId: readyInvoice?.fileId ?? null,
      proofFileId: readyProof?.fileId ?? null,
      proofRequirement: values.proofRequirement,
      title: values.title,
    });
  }

  return (
    <Dialog
      fullWidth
      maxWidth="sm"
      onClose={(_event, _reason) => {
        if (!locked) close();
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
            disabled={locked}
            error={Boolean(form.formState.errors.title)}
            helperText={form.formState.errors.title?.message}
            label="Título"
            slotProps={{ htmlInput: { maxLength: 200 } }}
            {...form.register("title")}
          />
          <TextField
            disabled={locked}
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
            {...form.register("amountReais", {
              // O erro de formato só vale para o valor submetido: editar o
              // campo já o descarta, sem esperar um novo envio válido.
              onChange: () => setAmountError(null),
            })}
          />
          <TextField
            disabled={locked}
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
            disabled={locked}
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
                disabled={locked}
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
            accept="application/pdf,image/jpeg,image/png,.pdf,.jpg,.jpeg,.png"
            disabled={locked}
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
          <AttachmentField
            accept="application/pdf,image/jpeg,image/png,.pdf,.jpg,.jpeg,.png"
            disabled={locked}
            label="Anexar comprovante"
            onChange={setProofAttachments}
            uploader={
              proofUploader ??
              (async () => ({
                ok: false as const,
                error: {
                  kind: "validation" as const,
                  message: "Anexo indisponível.",
                },
              }))
            }
            value={proofAttachments}
          />
          {failedAttachmentMessage ? (
            <Alert role="alert" severity="error">
              {failedAttachmentMessage}
            </Alert>
          ) : null}
          <TextField
            disabled={locked}
            error={Boolean(form.formState.errors.description)}
            helperText={form.formState.errors.description?.message}
            label="Descrição"
            minRows={3}
            multiline
            slotProps={{ htmlInput: { maxLength: 4000 } }}
            {...form.register("description")}
          />
          {recoveryRequired ? (
            <Alert aria-live="polite" role="status" severity="warning">
              Não foi possível confirmar o resultado da solicitação. Para evitar
              duplicidade, os dados permanecem bloqueados. Verifique e reenvie a
              mesma solicitação.
            </Alert>
          ) : errorMessage ? (
            <Alert role="alert" severity="error">
              {errorMessage}
            </Alert>
          ) : null}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button disabled={locked} onClick={close}>
          Cancelar
        </Button>
        {recoveryRequired ? (
          <Button disabled={submitting} onClick={onRetry} variant="contained">
            {submitting ? "Verificando…" : "Verificar e reenviar solicitação"}
          </Button>
        ) : (
          <Button
            disabled={isUploading || hasFailedAttachment || submitting}
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
        )}
      </DialogActions>
    </Dialog>
  );
}
