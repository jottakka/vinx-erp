import CloseOutlinedIcon from "@mui/icons-material/CloseOutlined";
import {
  Dialog,
  DialogContent,
  DialogTitle,
  IconButton,
  Stack,
  Typography,
} from "@mui/material";
import { type ReactNode, useId } from "react";

type EntityDialogProps = {
  children: ReactNode;
  description?: string;
  onClose: () => void;
  open: boolean;
  title: string;
};

/**
 * Modal padrão para criar ou visualizar uma entidade, substituindo formulários
 * inline. O MUI Dialog gerencia foco e aria-modal; o cabeçalho traz título,
 * descrição opcional e botão de fechar acessível.
 */
export function EntityDialog({
  children,
  description,
  onClose,
  open,
  title,
}: EntityDialogProps) {
  const descriptionId = useId();
  return (
    <Dialog
      aria-describedby={description ? descriptionId : undefined}
      fullWidth
      maxWidth="sm"
      onClose={onClose}
      open={open}
      slotProps={{
        paper: { sx: { borderRadius: { sm: 4, xs: 0 } } },
      }}
    >
      <DialogTitle sx={{ pb: 0 }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: "flex-start" }}>
          <Stack spacing={0.5} sx={{ flex: 1, minWidth: 0 }}>
            <Typography component="h2" variant="h5">
              {title}
            </Typography>
            {description ? (
              <Typography
                color="text.secondary"
                id={descriptionId}
                variant="body2"
              >
                {description}
              </Typography>
            ) : null}
          </Stack>
          <IconButton aria-label="Fechar" onClick={onClose} sx={{ mt: -0.5 }}>
            <CloseOutlinedIcon />
          </IconButton>
        </Stack>
      </DialogTitle>
      <DialogContent sx={{ pt: 2 }}>{children}</DialogContent>
    </Dialog>
  );
}
