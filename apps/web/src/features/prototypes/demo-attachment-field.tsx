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
    <Stack
      component="fieldset"
      spacing={1}
      sx={{ alignItems: "flex-start", border: 0, m: 0, minWidth: 0, p: 0 }}
    >
      <Typography component="legend" sx={{ p: 0 }} variant="subtitle2">
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
