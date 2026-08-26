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
import { useForm } from "react-hook-form";
import { z } from "zod";

export type AtendimentoCreateValues = z.infer<typeof atendimentoCreateSchema>;

const atendimentoCreateSchema = z.object({
  description: z.string().trim().min(1, "Informe uma descrição.").max(4000),
  title: z.string().trim().min(1, "Informe um título.").max(200),
});

type AtendimentoValues = z.output<typeof atendimentoCreateSchema>;

type AtendimentoCreateDialogProps = {
  onClose: () => void;
  onSubmit: (values: AtendimentoValues) => void;
  open: boolean;
};

/**
 * Modal de criação de cartão do Atendimento/Kanban. Substitui o formulário
 * inline; a validação local passa pelo backend na operação real.
 */
export function AtendimentoCreateDialog({
  onClose,
  onSubmit,
  open,
}: AtendimentoCreateDialogProps) {
  const form = useForm<AtendimentoValues>({
    defaultValues: { description: "", title: "" },
    resolver: zodResolver(atendimentoCreateSchema),
  });

  function close() {
    form.reset();
    onClose();
  }

  return (
    <Dialog open={open} onClose={close}>
      <DialogTitle>Novo atendimento</DialogTitle>
      <DialogContent>
        <Stack
          component="form"
          id="atendimento-create-form"
          onSubmit={form.handleSubmit((values) => {
            onSubmit(values);
            form.reset();
          })}
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
          Criar cartão
        </Button>
      </DialogActions>
    </Dialog>
  );
}
