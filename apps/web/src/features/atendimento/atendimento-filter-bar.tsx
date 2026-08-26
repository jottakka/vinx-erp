import {
  Checkbox,
  FormControlLabel,
  MenuItem,
  Stack,
  TextField,
} from "@mui/material";
import { PeoplePicker } from "./people-picker";
import { priorityLabel } from "./atendimento-lookup";
import {
  type AtendimentoColumn,
  type AtendimentoPriority,
  type QueueFilter,
  queueFilterToQuery,
} from "./atendimento-client";

export const columnChoices: readonly AtendimentoColumn[] = [
  "novo",
  "em_andamento",
  "concluido",
];
const priorityChoices: readonly AtendimentoPriority[] = [
  "critica",
  "alta",
  "media",
  "baixa",
];

/** Query string da URL ⇄ estado do filtro; URLs viram visões compartilháveis. */
export function filterFromSearchParams(search: string): QueueFilter {
  const params = new URLSearchParams(search);
  const status = params.get("status");
  const priority = params.get("priority");
  return {
    assigneeMembershipId: params.get("assigneeMembershipId") ?? undefined,
    overdue: params.get("overdue") === "true",
    priority:
      priority && priorityChoices.includes(priority as AtendimentoPriority)
        ? (priority as AtendimentoPriority)
        : undefined,
    search: params.get("search") ?? undefined,
    status:
      status && columnChoices.includes(status as AtendimentoColumn)
        ? (status as AtendimentoColumn)
        : undefined,
  };
}

export function replaceUrlWithFilter(filter: QueueFilter) {
  const qs = queueFilterToQuery(filter);
  const url = `${window.location.pathname}${qs}${window.location.hash}`;
  window.history.replaceState(null, "", url);
}

/**
 * Barra de filtros server-side compartilhável (status, prioridade,
 * responsável, atrasados e busca). A escrita na URL é feita por
 * history.replaceState para permanecer fiel e testável sem Router.
 */
export function AtendimentoFilterBar({
  companyId,
  disabled,
  filter,
  onChange,
}: {
  companyId: string;
  disabled?: boolean;
  filter: QueueFilter;
  onChange: (next: QueueFilter) => void;
}) {
  return (
    <Stack
      direction="row"
      spacing={1.5}
      sx={{ alignItems: "center", flexWrap: "wrap", opacity: disabled ? 0.6 : 1 }}
      useFlexGap
    >
      <TextField
        label="Status"
        onChange={(event) =>
          onChange({
            ...filter,
            status: (event.target.value || undefined) as AtendimentoColumn | undefined,
          })
        }
        select
        size="small"
        sx={{ minWidth: 150 }}
        value={filter.status ?? ""}
      >
        <MenuItem value="">&nbsp;</MenuItem>
        {columnChoices.map((column) => (
          <MenuItem key={column} value={column}>
            {column}
          </MenuItem>
        ))}
      </TextField>
      <TextField
        label="Prioridade"
        onChange={(event) =>
          onChange({
            ...filter,
            priority: (event.target.value || undefined) as AtendimentoPriority | undefined,
          })
        }
        select
        size="small"
        sx={{ minWidth: 140 }}
        value={filter.priority ?? ""}
      >
        <MenuItem value="">&nbsp;</MenuItem>
        {priorityChoices.map((candidate) => (
          <MenuItem key={candidate} value={candidate}>
            {priorityLabel(candidate)}
          </MenuItem>
        ))}
      </TextField>
      <PeoplePicker
        companyId={companyId}
        helperText="Filtro por responsável"
        label="Responsável"
        onChange={(membershipId) =>
          onChange({ ...filter, assigneeMembershipId: membershipId ?? undefined })
        }
        value={filter.assigneeMembershipId ?? null}
      />
      <FormControlLabel
        control={
          <Checkbox
            checked={filter.overdue}
            inputProps={{ "aria-label": "Somente atrasados" }}
            onChange={(event) => onChange({ ...filter, overdue: event.target.checked })}
          />
        }
        label="Atrasados"
      />
      <TextField
        defaultValue={filter.search ?? ""}
        label="Busca"
        onChange={(event) =>
          onChange({ ...filter, search: event.target.value || undefined })
        }
        placeholder="Título ou descrição"
        size="small"
        sx={{ minWidth: 200 }}
      />
    </Stack>
  );
}
