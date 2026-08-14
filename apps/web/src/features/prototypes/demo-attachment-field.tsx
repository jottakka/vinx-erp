import { Button, Stack, Typography } from "@mui/material";

export type DemoAttachmentState = "empty" | "processing" | "ready" | "failed";

type DemoAttachmentFieldProps = {
  label: string;
  onChange: (value: DemoAttachmentState) => void;
  required: boolean;
  value: DemoAttachmentState;
};

export function DemoAttachmentField({
  label,
  onChange,
  required,
  value,
}: DemoAttachmentFieldProps) {
  const nextState =
    value === "empty"
      ? "processing"
      : value === "processing"
        ? "ready"
        : "empty";

  return (
    <Stack spacing={1} sx={{ alignItems: "flex-start" }}>
      <Typography component="span" variant="subtitle2">
        {label}
        {required ? " (obrigatório)" : null}
      </Typography>
      <Typography aria-live="polite" variant="body2">
        Estado demonstrativo: {value}
      </Typography>
      <Button onClick={() => onChange(nextState)}>
        {value === "empty"
          ? "Adicionar arquivo de demonstração"
          : "Avançar estado demonstrativo"}
      </Button>
    </Stack>
  );
}
