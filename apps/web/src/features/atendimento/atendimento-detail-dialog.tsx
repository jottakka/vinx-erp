import AddOutlinedIcon from "@mui/icons-material/AddOutlined";
import {
  Autocomplete,
  Avatar,
  Box,
  Button,
  Chip,
  MenuItem,
  Stack,
  Tab,
  Tabs,
  TextField,
  Typography,
} from "@mui/material";
import { useEffect, useState } from "react";
import { EntityDialog } from "../../shared/ui/entity-dialog";
import {
  type AtendimentoCard,
  type AtendimentoPriority,
  type LookupChannel,
  type PersonOption,
  type TimelineEvent,
  atendimentoClient,
} from "./atendimento-client";
import { channelLabel, eventLabels, initialsFor, priorityLabel } from "./atendimento-lookup";
import { PeoplePicker } from "./people-picker";

const priorities: readonly AtendimentoPriority[] = ["critica", "alta", "media", "baixa"];

/**
 * Detalhe do cartão com abas: resumo + timeline (leitura), edição de campos
 * de ticket e observadores. Todo o estado de escrita usa versão otimista.
 */
export function AtendimentoDetailDialog({
  canAssign,
  companyId,
  card,
  categories,
  channels,
  onClose,
  onUpdated,
}: {
  canAssign?: boolean;
  card: AtendimentoCard | null;
  categories: { id: string; name: string; isActive: boolean }[];
  channels: LookupChannel[];
  companyId: string;
  onClose: () => void;
  onUpdated: (card: AtendimentoCard) => void;
}) {
  const [tab, setTab] = useState(0);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setTab(0);
    setError(null);
  }, [card?.id]);

  function replaced(next: AtendimentoCard) {
    onUpdated(next);
  }

  if (!card) {
    return (
      <EntityDialog
        description=""
        onClose={onClose}
        open={false}
        title="Visualizar atendimento"
      />
    );
  }

  return (
    <EntityDialog
      description={`Cartão de ${companyId ? "sua empresa" : "empresa"} · versão ${card.version}`}
      onClose={onClose}
      open
      title={card.title}
    >
      <Stack spacing={2}>
        <Tabs onChange={(_, nextTab: number) => setTab(nextTab)} value={tab}>
          <Tab label="Resumo" />
          <Tab label="Timeline" />
          <Tab label="Campos" />
          <Tab label="Observadores" />
        </Tabs>
        {error ? (
          <Typography color="error" variant="body2">
            {error}
          </Typography>
        ) : null}
        {tab === 0 ? (
          <SummaryTab canAssign={canAssign} card={card} companyId={companyId} onSaved={replaced} />
        ) : null}
        {tab === 1 ? (
          <TimelineTab card={card} companyId={companyId} />
        ) : null}
        {tab === 2 ? (
          <FieldsTab
            card={card}
            categories={categories.filter((category) => category.isActive)}
            channels={channels.filter((channel) => channel.isActive)}
            companyId={companyId}
            onError={setError}
            onSaved={replaced}
          />
        ) : null}
        {tab === 3 ? (
          <WatchersTab card={card} companyId={companyId} onSaved={replaced} />
        ) : null}
      </Stack>
    </EntityDialog>
  );
}

function SummaryTab({
  canAssign,
  card,
  companyId,
  onSaved,
}: {
  canAssign?: boolean;
  card: AtendimentoCard;
  companyId: string;
  onSaved: (card: AtendimentoCard) => void;
}) {
  const [assigneeMembershipId, setAssigneeMembershipId] = useState<string | null>(
    card.assigneeMembershipId,
  );
  const [assignError, setAssignError] = useState<string | null>(null);

  async function save() {
    if (!assigneeMembershipId) return;
    const result = await atendimentoClient.assign(
      companyId,
      card,
      assigneeMembershipId,
    );
    if (!result.ok) {
      setAssignError(
        result.code === "resource_not_found"
          ? "Responsável inexistente ou inativo nesta empresa."
          : "Não foi possível atribuir agora. Tente novamente.",
      );
      return;
    }
    setAssignError(null);
    onSaved(result.card);
  }

  return (
    <Stack spacing={1}>
      <Stack
        direction="row"
        spacing={1}
        sx={{ alignItems: "center", flexWrap: "wrap" }}
        useFlexGap
      >
        <PeoplePicker
          companyId={companyId}
          helperText="O cartão guarda apenas o membership-id internamente."
          label="Responsável"
          onChange={setAssigneeMembershipId}
          value={assigneeMembershipId}
        />
        {canAssign ? (
          <Button
            disabled={!assigneeMembershipId}
            onClick={() => void save()}
            startIcon={<PersonAddAltOutlinedIcon />}
            variant="outlined"
          >
            Atribuir responsável
          </Button>
        ) : null}
      </Stack>
      {assignError ? (
        <Typography color="error" variant="body2">
          {assignError}
        </Typography>
      ) : null}
      <Typography color="text.secondary">{card.description}</Typography>
      <Stack direction="row" spacing={0.75} sx={{ flexWrap: "wrap" }}>
        <Chip label={card.column} size="small" variant="outlined" />
        {card.priority ? (
          <Chip label={priorityLabel(card.priority)} size="small" variant="outlined" />
        ) : null}
        {card.overdue ? (
          <Chip color="error" label="Atrasado (SLA)" size="small" />
        ) : null}
        {card.slaDueAt ? (
          <Chip label={`SLA: ${new Date(card.slaDueAt).toLocaleString("pt-BR")}`} size="small" variant="outlined" />
        ) : null}
        {card.requesterPersonRef ? (
          <Chip label={`Solicitante: ${card.requesterPersonRef}`} size="small" variant="outlined" />
        ) : null}
      </Stack>
    </Stack>
  );
}

function TimelineTab({ card, companyId }: { card: AtendimentoCard; companyId: string }) {
  const [events, setEvents] = useState<TimelineEvent[] | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let active = true;
    void atendimentoClient.events(card.id, companyId).then((result) => {
      if (!active) return;
      if (result) setEvents(result);
      else setFailed(true);
    });
    return () => {
      active = false;
    };
  }, [card.id, companyId]);

  if (failed) {
    return (
      <Typography color="text.secondary" variant="body2">
        Não foi possível carregar a timeline.
      </Typography>
    );
  }

  return (
    <Stack spacing={1} component="ol" sx={{ m: 0, pl: 2 }}>
      {(events ?? []).map((event) => (
        <Box component="li" key={event.id}>
          <Typography variant="body2">
            <strong>{eventLabels(event.actionKind)}</strong>{" "}
            por {event.actorDisplayName} ·{" "}
            {new Date(event.occurredAt).toLocaleString("pt-BR")}
          </Typography>
        </Box>
      ))}
      {events && events.length === 0 ? (
        <Typography color="text.secondary" variant="body2">
          Nenhum evento registrado.
        </Typography>
      ) : null}
      {!events && !failed ? (
        <Typography color="text.secondary" role="status" variant="body2">
          Carregando eventos…
        </Typography>
      ) : null}
    </Stack>
  );
}

function FieldsTab({
  card,
  categories,
  channels,
  companyId,
  onError,
  onSaved,
}: {
  card: AtendimentoCard;
  categories: { id: string; name: string }[];
  channels: LookupChannel[];
  companyId: string;
  onError: (message: string | null) => void;
  onSaved: (card: AtendimentoCard) => void;
}) {
  const [priority, setPriority] = useState<AtendimentoPriority | "">(card.priority ?? "");
  const [categoryRefId, setCategoryRefId] = useState(card.categoryRefId ?? "");
  const [channelRefId, setChannelRefId] = useState(card.channelRefId ?? "");
  const [slaLocal, setSlaLocal] = useState(
    card.slaDueAt ? toLocalInputValue(new Date(card.slaDueAt)) : "",
  );
  const [requesterMembershipId, setRequesterMembershipId] = useState<string | null>(
    null,
  );
  const [mentioned, setMentioned] = useState<PersonOption[]>([]);
  const requesterPersonRef = card.requesterPersonRef ?? "";
  const [mentionOptions, setMentionOptions] = useState<PersonOption[]>([]);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    let active = true;
    void atendimentoClient.directoryOptions(companyId).then((result) => {
      if (active && result) setMentionOptions(result);
    });
    return () => {
      active = false;
    };
  }, [companyId]);

  async function save() {
    setSaving(true);
    onError(null);
    const slaIso =
      slaLocal
        ? new Date(slaLocal).toISOString()
        : undefined;
    const result = await atendimentoClient.fields(companyId, card, {
      categoryRefId: categoryRefId || undefined,
      channelRefId: channelRefId || undefined,
      mentionedMembershipIds:
        mentioned.length > 0 ? mentioned.map((option) => option.membershipId) : undefined,
      priority: priority ? priority : undefined,
      requesterPersonRef: requesterMembershipId ?? requesterPersonRef ?? undefined,
      slaDueAt: slaIso,
    });
    setSaving(false);
    if (!result.ok) return onError(messageForCode(result.code));
    onSaved(result.card);
  }

  return (
    <Stack spacing={2}>
      <TextField
        defaultValue={priority}
        fullWidth
        label="Prioridade"
        onChange={(event) =>
          setPriority(event.target.value as AtendimentoPriority | "")
        }
        select
        size="small"
      >
        <MenuItem value="">—</MenuItem>
        {priorities.map((candidate) => (
          <MenuItem key={candidate} value={candidate}>
            {priorityLabel(candidate)}
          </MenuItem>
        ))}
      </TextField>
      <TextField
        defaultValue={categoryRefId}
        fullWidth
        label="Categoria"
        onChange={(event) => setCategoryRefId(event.target.value)}
        select
        size="small"
      >
        <MenuItem value="">—</MenuItem>
        {categories.map((category) => (
          <MenuItem key={category.id} value={category.id}>
            {category.name}
          </MenuItem>
        ))}
      </TextField>
      <TextField
        defaultValue={channelRefId}
        fullWidth
        label="Canal"
        onChange={(event) => setChannelRefId(event.target.value)}
        select
        size="small"
      >
        <MenuItem value="">—</MenuItem>
        {channels.map((channel) => (
          <MenuItem key={channel.id} value={channel.id}>
            {channelLabel(channel.key)}
          </MenuItem>
        ))}
      </TextField>
      <TextField
        fullWidth
        helperText="Prazo do SLA; o atraso é derivado e nunca é armazenado."
        InputLabelProps={{ shrink: true }}
        label="SLA previsto"
        onChange={(event) => setSlaLocal(event.target.value)}
        size="small"
        type="datetime-local"
        value={slaLocal}
      />
      <PeoplePicker
        companyId={companyId}
        helperText="Referência opaca de solicitante quando vazio."
        label="Solicitante (picker)"
        onChange={setRequesterMembershipId}
        value={requesterMembershipId}
      />
      <Autocomplete<PersonOption, true, false, false>
        multiple
        options={mentionOptions}
        onChange={(_, value) => setMentioned(value)}
        renderInput={(params) => (
          <TextField
            {...params}
            label="Mencionar (avisa por notificação)"
            size="small"
          />
        )}
        renderTags={(value, getTagProps) =>
          value.map((option, index) => {
            const { key, ...tagProps } = getTagProps({ index });
            return (
              <Chip
                {...tagProps}
                avatar={<Avatar sx={{ fontSize: 12 }}>{initialsFor(option.displayName)}</Avatar>}
                key={key}
                label={option.displayName}
                size="small"
              />
            );
          })
        }
        value={mentioned}
      />
      <Button
        disabled={saving}
        onClick={() => void save()}
        startIcon={<AddOutlinedIcon />}
        variant="contained"
      >
        Salvar campos
      </Button>
    </Stack>
  );
}

function WatchersTab({
  card,
  companyId,
  onSaved,
}: {
  card: AtendimentoCard;
  companyId: string;
  onSaved: (card: AtendimentoCard) => void;
}) {
  const [watchers, setWatchers] = useState<PersonOption[]>(
    (card.watcherMembershipIds ?? []).map((membershipId) => ({
      displayName: membershipId,
      employeeId: membershipId,
      membershipId,
    })),
  );
  const [selected, setSelected] = useState<string | null>(null);
  const [names, setNames] = useState<PersonOption[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    void atendimentoClient.directoryOptions(companyId).then((result) => {
      if (active && result) setNames(result);
    });
    return () => {
      active = false;
    };
  }, [companyId]);

  async function add() {
    if (!selected) return;
    const result = await atendimentoClient.addWatcher(companyId, card, selected);
    if (!result.ok) return setError(messageForCode(result.code));
    setWatchers((current) =>
      current.some((watcher) => watcher.membershipId === selected)
        ? current
        : [
            ...current,
            names.find((option) => option.membershipId === selected) ?? {
              displayName: selected,
              employeeId: selected,
              membershipId: selected,
            },
          ],
    );
    onSaved(result.card);
  }

  async function remove(membershipId: string) {
    const result = await atendimentoClient.removeWatcher(
      companyId,
      card,
      membershipId,
    );
    if (!result.ok) return setError(messageForCode(result.code));
    setWatchers((current) =>
      current.filter((watcher) => watcher.membershipId !== membershipId),
    );
    onSaved(result.card);
  }

  return (
    <Stack spacing={1.5}>
      <PeoplePicker
        companyId={companyId}
        label="Adicionar observador"
        onChange={setSelected}
        value={selected}
      />
      <Button disabled={!selected} onClick={() => void add()} variant="outlined">
        Adicionar observador
      </Button>
      {error ? (
        <Typography color="error" variant="body2">
          {error}
        </Typography>
      ) : null}
      <Autocomplete<PersonOption, true, false, false>
        multiple
        options={names}
        readOnly
        renderTags={(value, getTagProps) =>
          value.map((option, index) => {
            const { key, ...tagProps } = getTagProps({ index });
            return (
              <Chip
                {...tagProps}
                avatar={<Avatar sx={{ fontSize: 12 }}>{initialsFor(option.displayName)}</Avatar>}
                key={key}
                label={option.displayName}
                onDelete={() => void remove(option.membershipId)}
                size="small"
              />
            );
          })
        }
        value={watchers}
      />
      {watchers.length === 0 ? (
        <Typography color="text.secondary" variant="body2">
          Nenhum observador. Observadores avisados são apenas os adicionados por aqui.
        </Typography>
      ) : null}
    </Stack>
  );
}

function messageForCode(code: string): string {
  if (code === "integrity_conflict")
    return "O cartão mudou no servidor. Reabra o detalhe para recarregar.";
  if (code === "resource_not_found") return "Referência inexistente ou inativa.";
  if (code === "capability_denied" || code === "membership_required")
    return "Sua sessão não tem permissão para esta ação nesta empresa.";
  return "Não foi possível concluir a ação. Tente novamente.";
}

function toLocalInputValue(date: Date): string {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}
