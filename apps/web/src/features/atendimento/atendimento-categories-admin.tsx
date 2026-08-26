import DeleteOutlineOutlinedIcon from "@mui/icons-material/DeleteOutlineOutlined";
import { Button, Chip, Stack, TextField, Typography } from "@mui/material";
import { useState } from "react";
import { atendimentoClient, type LookupCategory } from "./atendimento-client";

/**
 * Edição de categorias restrita a quem tem `service_desk.lookup.admin`;
 * o servidor nega o resto dos papéis de qualquer forma.
 */
export function CategoriesAdminSection({
  companyId,
  categories,
  onChanged,
  onError,
}: {
  companyId: string;
  categories: LookupCategory[];
  onChanged: () => void;
  onError: (message: string | null) => void;
}) {
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);

  async function create() {
    const trimmed = name.trim();
    if (!trimmed || !companyId) return;
    setBusy(true);
    onError(null);
    const result = await atendimentoClient.createCategory(companyId, trimmed);
    setBusy(false);
    if (!result.ok) return onError(messageFor(result.code ?? ""));
    setName("");
    onChanged();
  }

  async function deactivate(categoryId: string) {
    if (!companyId) return;
    setBusy(true);
    onError(null);
    const result = await atendimentoClient.deactivateCategory(
      companyId,
      categoryId,
    );
    setBusy(false);
    if (!result.ok) return onError(messageFor(result.code ?? ""));
    onChanged();
  }

  return (
    <Stack spacing={1}>
      <Typography variant="overline">Categorias (administração)</Typography>
      <Stack direction="row" spacing={1} useFlexGap>
        <TextField
          label="Nova categoria"
          onChange={(event) => setName(event.target.value)}
          size="small"
          value={name}
        />
        <Button disabled={busy || !name.trim()} onClick={() => void create()}>
          Criar categoria
        </Button>
      </Stack>
      <Stack
        direction="row"
        spacing={0.75}
        sx={{ flexWrap: "wrap" }}
        useFlexGap
      >
        {(categories ?? []).map((category) => (
          <Chip
            color={category.isActive ? "default" : "secondary"}
            key={category.id}
            label={`${category.name}${category.isActive ? "" : " (inativa)"}`}
            onDelete={
              category.isActive ? () => void deactivate(category.id) : undefined
            }
            deleteIcon={
              <DeleteOutlineOutlinedIcon
                aria-label={`Desativar ${category.name}`}
              />
            }
            variant="outlined"
          />
        ))}
        {(categories ?? []).length === 0 ? (
          <Typography color="text.secondary" variant="body2">
            Nenhuma categoria criada nesta empresa.
          </Typography>
        ) : null}
      </Stack>
    </Stack>
  );
}

function messageFor(code: string): string {
  if (code === "lookup_conflict")
    return "Já existe uma categoria com este nome nesta empresa.";
  if (code === "capability_denied" || code === "membership_required")
    return "Somente administradores do Atendimento gerenciam categorias.";
  return "Não foi possível concluir a ação. Tente novamente.";
}
