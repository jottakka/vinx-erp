import {
  Checkbox,
  FormControlLabel,
  MenuItem,
  Stack,
  TextField,
} from "@mui/material";
import { useEffect, useRef, useState } from "react";
import { isCanonicalUuid } from "../../shared/http/bff-response";
import {
  type AtendimentoColumn,
  type AtendimentoPriority,
  type QueueFilter,
  queueFilterToQuery,
} from "./atendimento-client";
import { priorityLabel } from "./atendimento-lookup";
import { PeoplePicker } from "./people-picker";

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
  const assigneeMembershipId = params.get("assigneeMembershipId");
  return {
    // Fail-closed: só aceita (e reescreve na URL) membership-id UUID canônico.
    assigneeMembershipId:
      assigneeMembershipId && isCanonicalUuid(assigneeMembershipId)
        ? assigneeMembershipId
        : undefined,
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
    <SearchDebouncedInner
      companyId={companyId}
      disabled={disabled}
      filter={filter}
      onChange={onChange}
    />
  );
}

function SearchDebouncedInner({
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
  // Busca com debounce curto para não disparar leitura por tecla.
  const [searchTerm, setSearchTerm] = useState(filter.search ?? "");
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    setSearchTerm(filter.search ?? "");
  }, [filter.search]);

  useEffect(
    () => () => {
      if (timerRef.current) clearTimeout(timerRef.current);
    },
    [],
  );

  function handleSearch(next: string) {
    setSearchTerm(next);
    if (timerRef.current) clearTimeout(timerRef.current);
    timerRef.current = setTimeout(() => {
      onChange({ ...filter, search: next.trim() || undefined });
    }, 300);
  }

  return (
    <Stack
      aria-busy={disabled === true}
      direction="row"
      spacing={1.5}
      sx={{
        alignItems: "center",
        flexWrap: "wrap",
        opacity: disabled ? 0.6 : 1,
      }}
      useFlexGap
    >
      <TextField
        disabled={disabled}
        label="Status"
        onChange={(event) =>
          onChange({
            ...filter,
            status: (event.target.value || undefined) as
              | AtendimentoColumn
              | undefined,
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
        disabled={disabled}
        label="Prioridade"
        onChange={(event) =>
          onChange({
            ...filter,
            priority: (event.target.value || undefined) as
              | AtendimentoPriority
              | undefined,
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
        disabled={disabled}
        label="Filtrar por responsável"
        onChange={(membershipId) =>
          onChange({
            ...filter,
            assigneeMembershipId: membershipId ?? undefined,
          })
        }
        value={filter.assigneeMembershipId ?? null}
      />
      <FormControlLabel
        control={
          <Checkbox
            aria-label="Somente atrasados"
            checked={filter.overdue}
            onChange={(event) =>
              onChange({ ...filter, overdue: event.target.checked })
            }
          />
        }
        disabled={disabled}
        label="Atrasados"
      />
      <TextField
        disabled={disabled}
        label="Busca"
        onChange={(event) => handleSearch(event.target.value)}
        placeholder="Título ou descrição"
        size="small"
        sx={{ minWidth: 200 }}
        value={searchTerm}
      />
    </Stack>
  );
}
