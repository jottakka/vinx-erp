import { zodResolver } from "@hookform/resolvers/zod";
import {
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  FormHelperText,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  TextField,
} from "@mui/material";
import { Controller, useForm } from "react-hook-form";
import { z } from "zod";
import { paguaiReturnCategories } from "./paguai-prototype-types";

const draftSchema = z.object({
  amount: z.coerce.number().positive("Informe um valor maior que zero"),
  company: z.enum(["alfa", "beta"], { error: "Selecione uma empresa" }),
  description: z.string().trim().min(3, "Descreva a solicitação"),
});

const returnSchema = z.object({
  category: z.enum(paguaiReturnCategories, {
    error: "Selecione uma categoria",
  }),
  description: z.string().trim().min(3, "Descreva o motivo da devolução"),
});

type DraftInput = z.input<typeof draftSchema>;
export type PaguaiDraftValues = z.output<typeof draftSchema>;
export type PaguaiReturnValues = z.output<typeof returnSchema>;

type DraftDialogProps = {
  onClose: () => void;
  onSubmit: (values: PaguaiDraftValues) => void;
  open: boolean;
};

export function PaguaiDraftDialog({
  onClose,
  onSubmit,
  open,
}: DraftDialogProps) {
  const form = useForm<DraftInput, undefined, PaguaiDraftValues>({
    defaultValues: {
      amount: 0,
      company: "" as DraftInput["company"],
      description: "",
    },
    resolver: zodResolver(draftSchema),
  });

  function close() {
    form.reset();
    onClose();
  }

  function submit(values: PaguaiDraftValues) {
    onSubmit(values);
    form.reset();
  }

  return (
    <Dialog onClose={close} open={open}>
      <DialogTitle>Nova solicitação</DialogTitle>
      <DialogContent>
        <Stack
          component="form"
          id="paguai-draft-form"
          onSubmit={form.handleSubmit(submit)}
          spacing={2}
          sx={{ pt: 1 }}
        >
          <Controller
            control={form.control}
            name="company"
            render={({ field: { ref, ...field }, fieldState }) => (
              <FormControl error={Boolean(fieldState.error)} fullWidth>
                <InputLabel id="paguai-company-label">Empresa</InputLabel>
                <Select
                  {...field}
                  aria-describedby={
                    fieldState.error ? "paguai-company-error" : undefined
                  }
                  inputRef={ref}
                  label="Empresa"
                  labelId="paguai-company-label"
                  value={field.value ?? ""}
                >
                  <MenuItem value="alfa">Alfa</MenuItem>
                  <MenuItem value="beta">Beta</MenuItem>
                </Select>
                {fieldState.error ? (
                  <FormHelperText id="paguai-company-error">
                    {fieldState.error.message}
                  </FormHelperText>
                ) : null}
              </FormControl>
            )}
          />
          <Controller
            control={form.control}
            name="description"
            render={({ field, fieldState }) => (
              <TextField
                {...field}
                error={Boolean(fieldState.error)}
                helperText={fieldState.error?.message}
                label="Descrição"
              />
            )}
          />
          <Controller
            control={form.control}
            name="amount"
            render={({ field, fieldState }) => (
              <TextField
                {...field}
                error={Boolean(fieldState.error)}
                helperText={fieldState.error?.message}
                label="Valor"
                slotProps={{ htmlInput: { min: 0, step: "0.01" } }}
                type="number"
              />
            )}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>Cancelar</Button>
        <Button form="paguai-draft-form" type="submit" variant="contained">
          Salvar rascunho
        </Button>
      </DialogActions>
    </Dialog>
  );
}

type ReturnDialogProps = {
  onClose: () => void;
  onSubmit: (values: PaguaiReturnValues) => void;
  open: boolean;
};

export function PaguaiReturnDialog({
  onClose,
  onSubmit,
  open,
}: ReturnDialogProps) {
  const form = useForm<PaguaiReturnValues>({
    defaultValues: {
      category: "" as PaguaiReturnValues["category"],
      description: "",
    },
    resolver: zodResolver(returnSchema),
  });

  function close() {
    form.reset();
    onClose();
  }

  function submit(values: PaguaiReturnValues) {
    onSubmit(values);
    form.reset();
  }

  return (
    <Dialog onClose={close} open={open}>
      <DialogTitle>Devolver solicitação</DialogTitle>
      <DialogContent>
        <Stack
          component="form"
          id="paguai-return-form"
          onSubmit={form.handleSubmit(submit)}
          spacing={2}
          sx={{ pt: 1 }}
        >
          <Controller
            control={form.control}
            name="category"
            render={({ field: { ref, ...field }, fieldState }) => (
              <FormControl error={Boolean(fieldState.error)} fullWidth>
                <InputLabel id="paguai-return-category-label">
                  Categoria
                </InputLabel>
                <Select
                  {...field}
                  aria-describedby={
                    fieldState.error
                      ? "paguai-return-category-error"
                      : undefined
                  }
                  inputRef={ref}
                  label="Categoria"
                  labelId="paguai-return-category-label"
                  value={field.value ?? ""}
                >
                  {paguaiReturnCategories.map((category) => (
                    <MenuItem key={category} value={category}>
                      {category}
                    </MenuItem>
                  ))}
                </Select>
                {fieldState.error ? (
                  <FormHelperText id="paguai-return-category-error">
                    {fieldState.error.message}
                  </FormHelperText>
                ) : null}
              </FormControl>
            )}
          />
          <Controller
            control={form.control}
            name="description"
            render={({ field, fieldState }) => (
              <TextField
                {...field}
                error={Boolean(fieldState.error)}
                helperText={fieldState.error?.message}
                label="Descrição da devolução"
                multiline
              />
            )}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>Cancelar</Button>
        <Button form="paguai-return-form" type="submit" variant="contained">
          Confirmar devolução
        </Button>
      </DialogActions>
    </Dialog>
  );
}
