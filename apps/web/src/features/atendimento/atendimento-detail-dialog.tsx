import AddOutlinedIcon from "@mui/icons-material/AddOutlined";
import PersonAddAltOutlinedIcon from "@mui/icons-material/PersonAddAltOutlined";
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
  type AtendimentoCommandResult,
  type AtendimentoPriority,
  atendimentoClient,
  type LookupChannel,
  type PersonOption,
  type TimelineEvent,
} from "./atendimento-client";
import {
  channelLabel,
  columnLabels,
  eventLabels,
  initialsFor,
  priorityLabel,
} from "./atendimento-lookup";
import { formatAtendimentoDate } from "./atendimento-presentation";
import { PeoplePicker } from "./people-picker";
import { useDirectoryOptions } from "./use-directory-options";

const priorities: readonly AtendimentoPriority[] = [
  "critica",
  "alta",
  "media",
  "baixa",
];

/** Estado neutro para referências de diretório não resolvidas. */
const unresolvedLabel = "Não resolvido";

/**
 * Executor de mutações fornecido pela página: lock por cartão, publicação
 * monotônica do snapshot e recuperação (409 current / readback ambíguo).
 * Retorna o snapshot publicado, ou null quando a ação não foi concluída.
 */
export type CardMutationExecutor = (
  card: AtendimentoCard,
  run: (card: AtendimentoCard) => Promise<AtendimentoCommandResult>,
) => Promise<AtendimentoCard | null>;

/**
 * Detalhe do cartão com abas: resumo + timeline (leitura), edição de campos
 * de ticket e observadores. Toda escrita passa pelo executor da página.
 */
export function AtendimentoDetailDialog({
  canAssign,
  companyId,
  card,
  categories,
  channels,
  onClose,
  onMutate,
}: {
  canAssign?: boolean;
  card: AtendimentoCard | null;
  categories: { id: string; name: string; isActive: boolean }[];
  channels: LookupChannel[];
  companyId: string;
  onClose: () => void;
  onMutate: CardMutationExecutor;
}) {
  const [tab, setTab] = useState(0);
  const [error, setError] = useState<string | null>(null);
  // Reset ao trocar de cartão durante o próprio render (padrão React para
  // ajustar estado quando a prop muda), sem efeito e sem deps extras.
  const [resetForId, setResetForId] = useState<string | null>(null);
  if (card && resetForId !== card.id) {
    setResetForId(card.id);
    setTab(0);
    setError(null);
  }

  if (!card) {
    return (
      <EntityDialog
        description=""
        onClose={onClose}
        open={false}
        title="Visualizar atendimento"
      >
        <span />
      </EntityDialog>
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
          <SummaryTab
            canAssign={canAssign}
            card={card}
            companyId={companyId}
            onMutate={onMutate}
          />
        ) : null}
        {tab === 1 ? <TimelineTab card={card} companyId={companyId} /> : null}
        {tab === 2 ? (
          <FieldsTab
            card={card}
            categories={categories.filter((category) => category.isActive)}
            channels={channels.filter((channel) => channel.isActive)}
            companyId={companyId}
            onError={setError}
            onMutate={onMutate}
          />
        ) : null}
        {tab === 3 ? (
          <WatchersTab card={card} companyId={companyId} onMutate={onMutate} />
        ) : null}
      </Stack>
    </EntityDialog>
  );
}

function SummaryTab({
  canAssign,
  card,
  companyId,
  onMutate,
}: {
  canAssign?: boolean;
  card: AtendimentoCard;
  companyId: string;
  onMutate: CardMutationExecutor;
}) {
  const [assigneeMembershipId, setAssigneeMembershipId] = useState<
    string | null
  >(card.assigneeMembershipId);
  const [assignError, setAssignError] = useState<string | null>(null);
  const [assigning, setAssigning] = useState(false);

  async function save() {
    if (!assigneeMembershipId || assigning) return;
    setAssigning(true);
    setAssignError(null);
    try {
      // Atribuição pelo executor da página: lock por cartão, snapshot 409 e
      // readback de resultado ambíguo vêm de lá; falhas publicam na fila.
      const updated = await onMutate(card, (current) =>
        atendimentoClient.assign(companyId, current, assigneeMembershipId),
      );
      if (!updated) {
        setAssignError(
          "A atribuição não foi concluída. O estado atual foi sincronizado; tente novamente.",
        );
      }
    } finally {
      setAssigning(false);
    }
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
          disabled={assigning}
          helperText="O cartão guarda apenas o membership-id internamente."
          label="Responsável"
          onChange={setAssigneeMembershipId}
          value={assigneeMembershipId}
        />
        {canAssign ? (
          <Button
            disabled={!assigneeMembershipId || assigning}
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
        <Chip
          label={columnLabels[card.column]}
          size="small"
          variant="outlined"
        />
        {card.priority ? (
          <Chip
            label={priorityLabel(card.priority)}
            size="small"
            variant="outlined"
          />
        ) : null}
        <Chip
          label={channelLabel(card.channel)}
          size="small"
          variant="outlined"
        />
        {card.isOverdue ? (
          <Chip color="error" label="Atrasado" size="small" />
        ) : null}
        {formatAtendimentoDate(card.slaDueAt) ? (
          <Chip
            label={`Prazo: ${formatAtendimentoDate(card.slaDueAt)}`}
            size="small"
            variant="outlined"
          />
        ) : null}
        {card.requesterPersonRef ? (
          <Chip
            label={`Solicitante: ${unresolvedLabel}`}
            size="small"
            variant="outlined"
          />
        ) : null}
      </Stack>
    </Stack>
  );
}

function TimelineTab({
  card,
  companyId,
}: {
  card: AtendimentoCard;
  companyId: string;
}) {
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
            <strong>{eventLabels(event.actionKind)}</strong> por{" "}
            {event.actorDisplayName} ·{" "}
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
  onMutate,
}: {
  card: AtendimentoCard;
  categories: { id: string; name: string }[];
  channels: LookupChannel[];
  companyId: string;
  onError: (message: string | null) => void;
  onMutate: CardMutationExecutor;
}) {
  const [priority, setPriority] = useState<AtendimentoPriority | "">(
    card.priority ?? "",
  );
  const [categoryRefId, setCategoryRefId] = useState(card.categoryRefId ?? "");
  const [channelRefId, setChannelRefId] = useState(card.channelRefId ?? "");
  const [slaLocal, setSlaLocal] = useState(
    card.slaDueAt ? toLocalInputValue(new Date(card.slaDueAt)) : "",
  );
  const [requesterMembershipId, setRequesterMembershipId] = useState<
    string | null
  >(null);
  const [mentioned, setMentioned] = useState<PersonOption[]>([]);
  const requesterPersonRef = card.requesterPersonRef ?? "";
  const { options: mentionOptions } = useDirectoryOptions(companyId);
  const [saving, setSaving] = useState(false);

  async function save() {
    if (saving) return;
    setSaving(true);
    onError(null);
    try {
      const slaIso = slaLocal ? new Date(slaLocal).toISOString() : undefined;
      // Escrita pelo executor da página: 409 current aplica o snapshot,
      // resultado ambíguo reconcilia por readback e o lock por cartão impede
      // mutação concorrente enquanto o fields está pendente.
      const updated = await onMutate(card, (current) =>
        atendimentoClient.fields(companyId, current, {
          categoryRefId: categoryRefId || undefined,
          channelRefId: channelRefId || undefined,
          mentionedMembershipIds:
            mentioned.length > 0
              ? mentioned.map((option) => option.membershipId)
              : undefined,
          priority: priority ? priority : undefined,
          requesterPersonRef:
            requesterMembershipId ?? requesterPersonRef ?? undefined,
          slaDueAt: slaIso,
        }),
      );
      if (!updated) {
        onError(
          "O salvamento não foi concluído. O estado atual foi sincronizado; tente novamente.",
        );
      }
    } finally {
      setSaving(false);
    }
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
        label="SLA previsto"
        slotProps={{ inputLabel: { shrink: true } }}
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
        onChange={(_, value) => setMentioned(value)}
        options={mentionOptions}
        renderInput={(params) => (
          <TextField
            {...params}
            label="Mencionar (avisa por notificação)"
            size="small"
          />
        )}
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
  onMutate,
}: {
  card: AtendimentoCard;
  companyId: string;
  onMutate: CardMutationExecutor;
}) {
  // Observadores são membership-ids do servidor; nomes vêm só do diretório da
  // empresa (fail-closed: referência não resolvida nunca vira rótulo).
  const [watcherIds, setWatcherIds] = useState<string[]>(
    card.watcherMembershipIds ?? [],
  );
  const [selected, setSelected] = useState<string | null>(null);
  const { options: names } = useDirectoryOptions(companyId);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  function resolvedName(membershipId: string): string | null {
    return (
      names.find((option) => option.membershipId === membershipId)
        ?.displayName ?? null
    );
  }

  async function add() {
    if (!selected || saving) return;
    setSaving(true);
    setError(null);
    try {
      const updated = await onMutate(card, (current) =>
        atendimentoClient.addWatcher(companyId, current, selected),
      );
      if (!updated) {
        setError(
          "A ação não foi concluída. O estado atual foi sincronizado; tente novamente.",
        );
        return;
      }
      // NoChange idempotente devolve o mesmo snapshot: a versão não sobe.
      setWatcherIds(updated.watcherMembershipIds ?? []);
      setSelected(null);
    } finally {
      setSaving(false);
    }
  }

  async function remove(membershipId: string) {
    if (saving) return;
    setSaving(true);
    setError(null);
    try {
      const updated = await onMutate(card, (current) =>
        atendimentoClient.removeWatcher(companyId, current, membershipId),
      );
      if (!updated) {
        setError(
          "A ação não foi concluída. O estado atual foi sincronizado; tente novamente.",
        );
        return;
      }
      setWatcherIds(updated.watcherMembershipIds ?? []);
    } finally {
      setSaving(false);
    }
  }

  return (
    <Stack spacing={1.5}>
      <PeoplePicker
        companyId={companyId}
        disabled={saving}
        label="Adicionar observador"
        onChange={setSelected}
        value={selected}
      />
      <Button
        disabled={!selected || saving}
        onClick={() => void add()}
        variant="outlined"
      >
        Adicionar observador
      </Button>
      {error ? (
        <Typography color="error" variant="body2">
          {error}
        </Typography>
      ) : null}
      <Stack
        direction="row"
        spacing={0.75}
        sx={{ flexWrap: "wrap" }}
        useFlexGap
      >
        {watcherIds.map((membershipId) => {
          const displayName = resolvedName(membershipId);
          return (
            <Chip
              avatar={
                <Avatar sx={{ fontSize: 12 }}>
                  {displayName ? initialsFor(displayName) : "?"}
                </Avatar>
              }
              key={membershipId}
              label={displayName ?? unresolvedLabel}
              onDelete={() => void remove(membershipId)}
              size="small"
            />
          );
        })}
      </Stack>
      {watcherIds.length === 0 ? (
        <Typography color="text.secondary" variant="body2">
          Nenhum observador. Observadores avisados são apenas os adicionados por
          aqui.
        </Typography>
      ) : null}
    </Stack>
  );
}

function toLocalInputValue(date: Date): string {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}
