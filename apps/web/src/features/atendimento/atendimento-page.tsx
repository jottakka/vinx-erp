import AddOutlinedIcon from "@mui/icons-material/AddOutlined";
import DragIndicatorOutlinedIcon from "@mui/icons-material/DragIndicatorOutlined";
import MoreVertOutlinedIcon from "@mui/icons-material/MoreVertOutlined";
import VisibilityOutlinedIcon from "@mui/icons-material/VisibilityOutlined";
import {
  Alert,
  Box,
  Button,
  Card,
  CardActions,
  CardContent,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  IconButton,
  Menu,
  MenuItem,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from "@mui/material";
import { useEffect, useMemo, useRef, useState } from "react";
import { AuthorizedCompanyFilter } from "../../shared/ui/authorized-company-filter";
import { CompanyProvider } from "../../shared/ui/company-context";
import { CompanyIdentity } from "../../shared/ui/company-identity";
import { EntityDialog } from "../../shared/ui/entity-dialog";
import { PageHeader } from "../../shared/ui/page-header";
import { StatusChip } from "../../shared/ui/status-chip";
import {
  hasCompanyCapability,
  resolveCompanyEntityPermissions,
} from "../auth/company-capabilities";
import { useSession } from "../auth/session-provider";
import {
  type AtendimentoAssignmentOption,
  type AtendimentoCard,
  type AtendimentoChannel,
  type AtendimentoColumn,
  type AtendimentoPriority,
  atendimentoClient,
} from "./atendimento-client";
import {
  type AtendimentoAttachmentUploader,
  AtendimentoCreateDialog,
} from "./atendimento-create-dialog";
import {
  channelLabels,
  formatAtendimentoDate,
  isAtendimentoOverdue,
  priorityPresentation,
} from "./atendimento-presentation";

const columns: readonly {
  id: AtendimentoColumn;
  label: string;
  tone: "info" | "primary" | "success" | "error";
}[] = [
  { id: "novo", label: "Novos", tone: "info" },
  { id: "em_andamento", label: "Em andamento", tone: "primary" },
  { id: "concluido", label: "Concluídos", tone: "success" },
  { id: "cancelado", label: "Cancelados", tone: "error" },
];
const workflowColumns = columns.filter((column) => column.id !== "cancelado");

const readCapability = "service_desk.ticket.read_queue";
const createCapability = "service_desk.ticket.create";
const moveCapability = "service_desk.ticket.move";
const assignCapability = "service_desk.ticket.assign";
const cancelCapability = "service_desk.ticket.cancel";

type CardMenuState = {
  anchorEl: HTMLElement;
  card: AtendimentoCard;
};

type CardMutationLease = {
  key: string;
  generation: number;
};

function messageFor(code: string) {
  if (code === "integrity_conflict")
    return "O cartão foi atualizado enquanto você atuava. Verifique o estado atual da fila.";
  if (code === "capability_denied" || code === "membership_required")
    return "Sua sessão não tem permissão para esta ação nesta empresa.";
  return "Não foi possível concluir a ação. Tente novamente.";
}

export function AtendimentoPage({
  attachmentUploader,
  title = "Atendimento",
}: {
  attachmentUploader?: AtendimentoAttachmentUploader;
  title?: string;
}) {
  const { bootstrap } = useSession();
  const sessionCompanies = useMemo(
    () => bootstrap?.companies ?? [],
    [bootstrap],
  );
  const companies = useMemo(
    () =>
      sessionCompanies.filter((company) =>
        company.capabilities.some(
          (capability) =>
            capability.name === readCapability &&
            capability.scopes.includes("Company"),
        ),
      ),
    [sessionCompanies],
  );
  const [companyId, setCompanyId] = useState("");
  const [selectedCompanyIds, setSelectedCompanyIds] = useState<
    readonly string[]
  >([]);
  const [cards, setCards] = useState<AtendimentoCard[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [createOpen, setCreateOpen] = useState(false);
  const [statusMenu, setStatusMenu] = useState<CardMenuState | null>(null);
  const [actionsMenu, setActionsMenu] = useState<CardMenuState | null>(null);
  const [viewingCard, setViewingCard] = useState<AtendimentoCard | null>(null);
  const [assigningCard, setAssigningCard] = useState<AtendimentoCard | null>(
    null,
  );
  const [assignmentOptions, setAssignmentOptions] = useState<
    readonly AtendimentoAssignmentOption[]
  >([]);
  const [createOptions, setCreateOptions] = useState<
    readonly AtendimentoAssignmentOption[]
  >([]);
  const [assigneeMembershipId, setAssigneeMembershipId] = useState("");
  const [assigning, setAssigning] = useState(false);
  const [cancellingCard, setCancellingCard] = useState<AtendimentoCard | null>(
    null,
  );
  const [cancellationReason, setCancellationReason] = useState("");
  const [cancelling, setCancelling] = useState(false);
  const [draggedCardId, setDraggedCardId] = useState<string | null>(null);
  const [dropTarget, setDropTarget] = useState<AtendimentoColumn | null>(null);
  const [pendingMutationKeys, setPendingMutationKeys] = useState<
    readonly string[]
  >([]);
  const assignmentEpoch = useRef(0);
  const assignmentAbort = useRef<AbortController | null>(null);
  const mutationGenerations = useRef(new Map<string, number>());
  const pendingMutations = useRef(new Map<string, number>());
  const [recoveringCardKeys, setRecoveringCardKeys] = useState<
    readonly string[]
  >([]);
  const recoveryEpoch = useRef(new Map<string, number>());

  const createCompanies = useMemo(
    () =>
      sessionCompanies.filter((company) =>
        hasCompanyCapability(company, createCapability),
      ),
    [sessionCompanies],
  );
  const selectedCompany = useMemo(
    () => createCompanies.find((company) => company.companyId === companyId),
    [createCompanies, companyId],
  );
  const requestedCompanies = useMemo(() => {
    const authorizedIds = new Set(
      companies.map((company) => company.companyId),
    );
    const selected = selectedCompanyIds.filter((id) => authorizedIds.has(id));
    return selected.length
      ? companies.filter((company) => selected.includes(company.companyId))
      : companies;
  }, [companies, selectedCompanyIds]);
  const visibleCards = useMemo(() => {
    const requestedCompanyIds = new Set(
      requestedCompanies.map((company) => company.companyId),
    );
    return cards.filter((card) => requestedCompanyIds.has(card.companyId));
  }, [cards, requestedCompanies]);

  const permissions = useMemo(
    () =>
      resolveCompanyEntityPermissions(selectedCompany, {
        create: createCapability,
        edit: moveCapability,
      }),
    [selectedCompany],
  );

  useEffect(() => {
    if (!createCompanies.some((company) => company.companyId === companyId)) {
      setAssigneeMembershipId("");
      setCreateOptions([]);
      setCompanyId(createCompanies[0]?.companyId ?? "");
    }
  }, [companyId, createCompanies]);

  useEffect(() => {
    if (!requestedCompanies.length) {
      setCards([]);
      return;
    }
    let active = true;
    setLoading(true);
    setError(null);
    setViewingCard(null);
    void Promise.all(
      requestedCompanies.map(async (company) => ({
        companyId: company.companyId,
        cards: await atendimentoClient.list(company.companyId),
      })),
    ).then((results) => {
      if (!active) return;
      if (results.every((result) => result.cards !== null)) {
        const loadedCards = results.flatMap(
          (result) =>
            result.cards?.filter(
              (card) => card.companyId === result.companyId,
            ) ?? [],
        );
        // Hidratação monotônica na ordem canônica da resposta: para cada
        // companyId:cardId vale o snapshot de maior versão (um fila atrasada
        // nunca rebaixa), a ordem vem de loadedCards (createdAt DESC, id DESC)
        // e IDs ausentes da resposta são anexados depois. Sem o clear
        // intermediário, um sucesso de mutação publicado durante o loading
        // também não é descartado.
        setCards((current) => {
          const currentByKey = new Map(
            current.map((card) => [cardMutationKey(card), card]),
          );
          const merged = new Map<string, AtendimentoCard>();
          for (const loaded of loadedCards) {
            const key = cardMutationKey(loaded);
            const known = currentByKey.get(key);
            merged.set(
              key,
              !known || loaded.version >= known.version ? loaded : known,
            );
          }
          for (const card of current) {
            const key = cardMutationKey(card);
            if (!merged.has(key)) merged.set(key, card);
          }
          return [...merged.values()];
        });
      } else setError("Não foi possível carregar a fila. Tente novamente.");
      setLoading(false);
    });
    return () => {
      active = false;
    };
  }, [requestedCompanies]);

  useEffect(() => {
    const controller = new AbortController();
    setCreateOptions([]);
    if (!createOpen || !companyId || !permissions.canCreate)
      return () => controller.abort();
    void atendimentoClient
      .assignmentOptions(companyId, controller.signal)
      .then((options) => {
        if (!controller.signal.aborted && options) setCreateOptions(options);
      });
    return () => controller.abort();
  }, [companyId, createOpen, permissions.canCreate]);

  async function createCard(values: {
    requestId: string;
    title: string;
    description: string;
    attachmentFileId?: string;
    participantMembershipIds: readonly string[];
    priority: AtendimentoPriority;
    channel: AtendimentoChannel;
    slaDueAt: string | null;
  }): Promise<{ ok: true } | { ok: false; message: string }> {
    if (!selectedCompany || !permissions.canCreate)
      return {
        message: "Não foi possível criar o atendimento. Tente novamente.",
        ok: false,
      };
    const result = await atendimentoClient.create(
      companyId,
      values.requestId,
      values.title,
      values.description,
      {
        attachmentFileId: values.attachmentFileId ?? null,
        channel: values.channel,
        participantMembershipIds: values.participantMembershipIds,
        priority: values.priority,
        slaDueAt: values.slaDueAt,
      },
    );
    if (!result.ok) {
      return { message: messageFor(result.code), ok: false };
    }
    setCards((current) => [...current, result.card]);
    return { ok: true };
  }

  function cardMutationKey(card: AtendimentoCard) {
    return `${card.companyId}:${card.id}`;
  }

  // Lock por cartão compartilhado por move/assign/cancel: nenhuma mutação do
  // mesmo companyId:cardId inicia enquanto outra estiver pendente, e uma
  // resposta só é publicada pela geração vigente.
  function beginCardMutation(card: AtendimentoCard): CardMutationLease | null {
    const key = cardMutationKey(card);
    if (pendingMutations.current.has(key)) return null;
    const generation = (mutationGenerations.current.get(key) ?? 0) + 1;
    mutationGenerations.current.set(key, generation);
    pendingMutations.current.set(key, generation);
    setPendingMutationKeys([...pendingMutations.current.keys()]);
    return { generation, key };
  }

  function finishCardMutation(lease: CardMutationLease) {
    if (pendingMutations.current.get(lease.key) !== lease.generation) return;
    pendingMutations.current.delete(lease.key);
    setPendingMutationKeys([...pendingMutations.current.keys()]);
  }

  function isCardMutationPending(card: AtendimentoCard) {
    return pendingMutationKeys.includes(cardMutationKey(card));
  }

  // Publicador único de cartões: nunca reduz a versão de uma entidade da
  // mesma empresa/cartão, em nenhuma cópia aberta.
  function applyCardSnapshot(
    incoming: AtendimentoCard,
    lease?: CardMutationLease,
  ) {
    if (lease && pendingMutations.current.get(lease.key) !== lease.generation)
      return;
    const replaces = (card: AtendimentoCard) =>
      card.companyId === incoming.companyId && card.id === incoming.id
        ? incoming.version >= card.version
          ? incoming
          : card
        : card;
    setCards((current) => current.map(replaces));
    setStatusMenu((menu) =>
      menu ? { ...menu, card: replaces(menu.card) } : menu,
    );
    setActionsMenu((menu) =>
      menu ? { ...menu, card: replaces(menu.card) } : menu,
    );
    setViewingCard((card) => (card ? replaces(card) : card));
    setAssigningCard((card) => (card ? replaces(card) : card));
    setCancellingCard((card) => (card ? replaces(card) : card));
  }

  async function move(card: AtendimentoCard, column: AtendimentoColumn) {
    const key = recoveryKey(card.companyId, card.id);
    if (
      card.column === column ||
      card.column === "cancelado" ||
      recoveringCardKeys.includes(key)
    )
      return;
    const lease = beginCardMutation(card);
    if (!lease) return;
    try {
      const result = await atendimentoClient.move(card.companyId, card, column);
      if (!result.ok)
        return await recover(
          result.code,
          result.current,
          card.companyId,
          card.id,
        );
      applyCardSnapshot(result.card, lease);
    } finally {
      finishCardMutation(lease);
    }
  }

  async function assign(card: AtendimentoCard) {
    if (!assigneeMembershipId.trim()) return;
    const lease = beginCardMutation(card);
    if (!lease) return;
    setAssigning(true);
    try {
      const result = await atendimentoClient.assign(
        card.companyId,
        card,
        assigneeMembershipId.trim(),
      );
      if (!result.ok) {
        await recover(result.code, result.current, card.companyId, card.id);
        return;
      }
      applyCardSnapshot(result.card, lease);
      setAssigningCard(null);
      setAssigneeMembershipId("");
    } finally {
      finishCardMutation(lease);
      setAssigning(false);
    }
  }

  async function openAssignment(card: AtendimentoCard) {
    assignmentAbort.current?.abort();
    const controller = new AbortController();
    assignmentAbort.current = controller;
    const expectedEpoch = ++assignmentEpoch.current;
    setAssigneeMembershipId("");
    setAssignmentOptions([]);
    setAssigningCard(card);
    const options = await atendimentoClient.assignmentOptions(
      card.companyId,
      controller.signal,
    );
    if (assignmentEpoch.current === expectedEpoch && options)
      setAssignmentOptions(options);
    else if (assignmentEpoch.current === expectedEpoch)
      setError("Não foi possível carregar as pessoas autorizadas.");
  }

  async function cancel(card: AtendimentoCard) {
    const reason = cancellationReason.trim();
    if (!reason || reason.length > 500) return;
    const lease = beginCardMutation(card);
    if (!lease) return;
    setCancelling(true);
    try {
      const result = await atendimentoClient.cancel(
        card.companyId,
        card,
        reason,
      );
      if (!result.ok) {
        await recover(result.code, result.current, card.companyId, card.id);
        return;
      }
      applyCardSnapshot(result.card, lease);
      setCancellingCard(null);
      setCancellationReason("");
    } finally {
      finishCardMutation(lease);
      setCancelling(false);
    }
  }

  function applyRecoveredCard(recovered: AtendimentoCard) {
    applyCardSnapshot(recovered);
  }

  async function reloadAfterConflict(
    code: string,
    companyId: string,
    cardId: string,
    key: string,
    epoch: number,
  ) {
    const refreshedCards = await atendimentoClient.list(companyId);
    if (recoveryEpoch.current.get(key) !== epoch) return;
    if (!refreshedCards) {
      invalidateCard(companyId, cardId);
      setError(
        "Não foi possível sincronizar a fila. Atualize a página e tente novamente.",
      );
      return;
    }
    const refreshed = refreshedCards.find(
      (item) => item.id === cardId && item.companyId === companyId,
    );
    if (refreshed) {
      applyRecoveredCard(refreshed);
      setError(messageFor(code));
      return;
    }
    invalidateCard(companyId, cardId);
    setError("O atendimento não está mais disponível nesta empresa.");
  }

  function recoveryKey(companyId: string, cardId: string) {
    return `${companyId}:${cardId}`;
  }

  function invalidateCard(companyId: string, cardId: string) {
    setCards((cards) =>
      cards.filter(
        (card) => card.id !== cardId || card.companyId !== companyId,
      ),
    );
    setViewingCard((card) =>
      card?.id === cardId && card.companyId === companyId ? null : card,
    );
    setAssigningCard((card) =>
      card?.id === cardId && card.companyId === companyId ? null : card,
    );
    setCancellingCard((card) =>
      card?.id === cardId && card.companyId === companyId ? null : card,
    );
  }

  async function recover(
    code: string,
    current: AtendimentoCard | undefined,
    companyId: string,
    cardId: string,
  ): Promise<void> {
    const key = recoveryKey(companyId, cardId);
    const epoch = (recoveryEpoch.current.get(key) ?? 0) + 1;
    recoveryEpoch.current.set(key, epoch);
    setRecoveringCardKeys((keys) =>
      keys.includes(key) ? keys : [...keys, key],
    );
    try {
      const validCurrent =
        current?.id === cardId && current.companyId === companyId;
      if (validCurrent) {
        applyRecoveredCard(current);
        setError(messageFor(code));
        return;
      }
      if (code === "integrity_conflict") {
        await reloadAfterConflict(code, companyId, cardId, key, epoch);
        return;
      }
      setError(messageFor(code));
    } finally {
      if (recoveryEpoch.current.get(key) === epoch) {
        recoveryEpoch.current.delete(key);
        setRecoveringCardKeys((keys) => keys.filter((item) => item !== key));
      }
    }
  }

  function handleDrop(column: AtendimentoColumn) {
    if (!draggedCardId) return;
    const card = cards.find((item) => item.id === draggedCardId);
    if (card && column !== "cancelado" && card.column !== column)
      void move(card, column);
    setDraggedCardId(null);
    setDropTarget(null);
  }

  function canEditCard(card: AtendimentoCard) {
    return resolveCompanyEntityPermissions(
      companies.find((company) => company.companyId === card.companyId),
      { edit: moveCapability },
    ).canEdit;
  }

  function canAssignCard(card: AtendimentoCard) {
    return (
      companies
        .find((company) => company.companyId === card.companyId)
        ?.capabilities.some(
          (capability) =>
            capability.name === assignCapability &&
            capability.scopes.includes("Company"),
        ) ?? false
    );
  }

  function canCancelCard(card: AtendimentoCard) {
    return (
      companies
        .find((company) => company.companyId === card.companyId)
        ?.capabilities.some(
          (capability) =>
            capability.name === cancelCapability &&
            capability.scopes.includes("Company"),
        ) ?? false
    );
  }

  return (
    <CompanyProvider company={selectedCompany ?? null}>
      <Stack spacing={3}>
        <PageHeader
          actions={
            createCompanies.length ? (
              <Button
                onClick={() => setCreateOpen(true)}
                startIcon={<AddOutlinedIcon />}
                variant="contained"
              >
                Novo atendimento
              </Button>
            ) : (
              <Tooltip title="Sua sessão não pode criar atendimentos.">
                <span>
                  <Button
                    disabled
                    startIcon={<AddOutlinedIcon />}
                    variant="contained"
                  >
                    Novo atendimento
                  </Button>
                </span>
              </Tooltip>
            )
          }
          description="Acompanhe e organize os atendimentos das empresas que você pode acessar."
          title={title}
        />
        {error ? (
          <Alert onClose={() => setError(null)} severity="error">
            {error}
          </Alert>
        ) : null}
        <AuthorizedCompanyFilter
          companies={companies}
          disabled={loading}
          label="Filtrar empresas"
          onChange={setSelectedCompanyIds}
          selectedCompanyIds={selectedCompanyIds}
        />
        {!companies.length ? (
          <Alert severity="info">
            Sua sessão não possui empresa com acesso à fila de {title}.
          </Alert>
        ) : null}
        {loading ? (
          <Typography aria-live="polite" role="status">
            Carregando fila…
          </Typography>
        ) : null}
        <Box
          sx={{
            display: "grid",
            gap: 2,
            gridTemplateColumns: {
              lg: "repeat(4, 1fr)",
              md: "repeat(2, 1fr)",
              xs: "1fr",
            },
          }}
        >
          {columns.map((column) => (
            <Box
              key={column.id}
              onDragOver={(event) => {
                if (column.id === "cancelado") return;
                event.preventDefault();
                event.stopPropagation();
                setDropTarget(column.id);
              }}
              onDragLeave={(event) => {
                event.stopPropagation();
                // Só limpa o alvo ao sair realmente da coluna, não ao passar
                // por um cartão filho (relatedTarget dentro do contêiner).
                if (
                  event.relatedTarget instanceof Node &&
                  event.currentTarget.contains(event.relatedTarget)
                ) {
                  return;
                }
                setDropTarget((current) =>
                  current === column.id ? null : current,
                );
              }}
              onDrop={(event) => {
                if (column.id === "cancelado") return;
                event.preventDefault();
                event.stopPropagation();
                handleDrop(column.id);
              }}
              sx={{
                "&[data-state-tone]": {},
                bgcolor:
                  dropTarget === column.id
                    ? "action.selected"
                    : "background.paper",
                border: 1,
                borderColor:
                  dropTarget === column.id ? "primary.main" : "divider",
                borderRadius: 3,
                minHeight: 220,
                px: 1.5,
                py: 1.5,
                transition: (theme) =>
                  theme.transitions.create(
                    ["background-color", "border-color"],
                    {
                      duration: theme.transitions.duration.shortest,
                    },
                  ),
              }}
              data-state-tone={column.tone}
            >
              <Typography
                color="text.secondary"
                component="h2"
                sx={{ mb: 1.5, textTransform: "uppercase" }}
                variant="overline"
              >
                {column.label}
              </Typography>
              <Stack spacing={1.25}>
                {visibleCards
                  .filter((card) => card.column === column.id)
                  .map((card) => {
                    const mutationPending = isCardMutationPending(card);
                    const cardMovable =
                      canEditCard(card) &&
                      card.column !== "cancelado" &&
                      !mutationPending;
                    return (
                      <Card
                        key={card.id}
                        draggable={cardMovable}
                        onDragEnd={() => setDraggedCardId(null)}
                        onDragStart={() => setDraggedCardId(card.id)}
                        sx={{
                          cursor: cardMovable ? "grab" : "default",
                          "&:active": {
                            cursor: cardMovable ? "grabbing" : "default",
                          },
                        }}
                        variant="outlined"
                      >
                        <CardContent sx={{ pb: 1 }}>
                          <Stack
                            direction="row"
                            spacing={1}
                            sx={{ alignItems: "flex-start" }}
                          >
                            <Box sx={{ flex: 1, minWidth: 0 }}>
                              <Typography variant="subtitle1">
                                {card.title}
                              </Typography>
                              <Typography color="text.secondary">
                                {card.description}
                              </Typography>
                              <Box sx={{ mt: 1 }}>
                                <CompanyIdentity
                                  companyId={card.companyId}
                                  companyName={
                                    sessionCompanies.find(
                                      (company) =>
                                        company.companyId === card.companyId,
                                    )?.companyName ?? "Empresa autorizada"
                                  }
                                />
                              </Box>
                            </Box>
                            {cardMovable ? (
                              <DragIndicatorOutlinedIcon
                                aria-hidden
                                color="action"
                                fontSize="small"
                              />
                            ) : null}
                          </Stack>
                          <Stack
                            direction="row"
                            spacing={0.75}
                            sx={{ flexWrap: "wrap", mt: 1 }}
                          >
                            <Chip
                              color={
                                columns.find((item) => item.id === card.column)
                                  ?.tone
                              }
                              label={columnLabel(card.column)}
                              size="small"
                              variant="outlined"
                            />
                            {card.assigneeMembershipId ? (
                              <Chip
                                color="primary"
                                label="Responsável atribuído"
                                size="small"
                                variant="outlined"
                              />
                            ) : null}
                            <StatusChip
                              label={priorityPresentation[card.priority].label}
                              tone={priorityPresentation[card.priority].tone}
                            />
                            <Chip
                              label={channelLabels[card.channel]}
                              size="small"
                              variant="outlined"
                            />
                            {formatAtendimentoDate(card.slaDueAt) ? (
                              <Chip
                                label={`Prazo: ${formatAtendimentoDate(card.slaDueAt)}`}
                                size="small"
                                variant="outlined"
                              />
                            ) : null}
                            {isAtendimentoOverdue(card) ? (
                              <StatusChip label="Atrasado" tone="error" />
                            ) : null}
                          </Stack>
                        </CardContent>
                        <CardActions
                          sx={{
                            alignItems: "center",
                            flexWrap: "wrap",
                            gap: 0.5,
                            "& > :not(style) ~ :not(style)": { ml: 0 },
                          }}
                        >
                          <Button
                            onClick={() => setViewingCard(card)}
                            size="small"
                            startIcon={<VisibilityOutlinedIcon />}
                          >
                            Visualizar
                          </Button>
                          {canEditCard(card) && card.column !== "cancelado" ? (
                            <Button
                              disabled={mutationPending}
                              onClick={(event) =>
                                setStatusMenu({
                                  anchorEl: event.currentTarget,
                                  card,
                                })
                              }
                              size="small"
                            >
                              Alterar status
                            </Button>
                          ) : null}
                          {(canAssignCard(card) || canCancelCard(card)) &&
                          card.column !== "cancelado" ? (
                            <IconButton
                              aria-label="Mais ações"
                              disabled={mutationPending}
                              onClick={(event) =>
                                setActionsMenu({
                                  anchorEl: event.currentTarget,
                                  card,
                                })
                              }
                              size="small"
                            >
                              <MoreVertOutlinedIcon />
                            </IconButton>
                          ) : null}
                        </CardActions>
                      </Card>
                    );
                  })}
                {visibleCards.filter((card) => card.column === column.id)
                  .length === 0 ? (
                  <Typography color="text.secondary" variant="body2">
                    Nenhum cartão nesta coluna.
                  </Typography>
                ) : null}
              </Stack>
            </Box>
          ))}
        </Box>
      </Stack>

      <AtendimentoCreateDialog
        assignmentOptions={createOptions}
        companies={createCompanies}
        uploader={attachmentUploader}
        companyId={companyId}
        onClose={() => setCreateOpen(false)}
        onCompanyChange={(nextCompanyId) => {
          setAssigneeMembershipId("");
          setCreateOptions([]);
          setCompanyId(nextCompanyId);
        }}
        onSubmit={(values) => {
          return createCard(values);
        }}
        open={createOpen}
      />

      <Menu
        anchorEl={statusMenu?.anchorEl ?? null}
        onClose={() => setStatusMenu(null)}
        open={Boolean(statusMenu)}
      >
        {statusMenu
          ? workflowColumns
              .filter(
                (destination) => destination.id !== statusMenu.card.column,
              )
              .map((destination) => (
                <MenuItem
                  key={destination.id}
                  onClick={() => {
                    void move(statusMenu.card, destination.id);
                    setStatusMenu(null);
                  }}
                >
                  {destination.label}
                </MenuItem>
              ))
          : null}
      </Menu>
      <Menu
        anchorEl={actionsMenu?.anchorEl ?? null}
        onClose={() => setActionsMenu(null)}
        open={Boolean(actionsMenu)}
      >
        {actionsMenu && canAssignCard(actionsMenu.card) ? (
          <MenuItem
            onClick={() => {
              void openAssignment(actionsMenu.card);
              setActionsMenu(null);
            }}
          >
            Atribuir responsável
          </MenuItem>
        ) : null}
        {actionsMenu && canCancelCard(actionsMenu.card) ? (
          <MenuItem
            onClick={() => {
              setCancellingCard(actionsMenu.card);
              setActionsMenu(null);
            }}
          >
            Cancelar atendimento
          </MenuItem>
        ) : null}
      </Menu>

      <ViewCardDialog
        card={viewingCard}
        companyName={
          sessionCompanies.find(
            (company) => company.companyId === viewingCard?.companyId,
          )?.companyName ?? "Empresa autorizada"
        }
        onClose={() => setViewingCard(null)}
      />

      <Dialog
        fullWidth
        maxWidth="sm"
        onClose={() => {
          if (assigning) return;
          assignmentEpoch.current++;
          assignmentAbort.current?.abort();
          setAssigningCard(null);
        }}
        open={Boolean(assigningCard)}
      >
        <DialogTitle>Atribuir responsável</DialogTitle>
        <DialogContent>
          <TextField
            disabled={assigning}
            fullWidth
            label="Responsável"
            onChange={(event) => setAssigneeMembershipId(event.target.value)}
            select
            sx={{ mt: 1 }}
            value={assigneeMembershipId}
          >
            {assignmentOptions.map((option) => (
              <MenuItem key={option.membershipId} value={option.membershipId}>
                {option.displayName}
              </MenuItem>
            ))}
          </TextField>
        </DialogContent>
        <DialogActions>
          <Button
            disabled={assigning}
            onClick={() => {
              assignmentEpoch.current++;
              assignmentAbort.current?.abort();
              setAssigningCard(null);
            }}
          >
            Cancelar
          </Button>
          <Button
            disabled={
              assigning ||
              !assigningCard ||
              !assigneeMembershipId ||
              (assigningCard ? isCardMutationPending(assigningCard) : false)
            }
            onClick={() => assigningCard && void assign(assigningCard)}
            variant="contained"
          >
            {assigning ? "Atribuindo…" : "Confirmar atribuição"}
          </Button>
        </DialogActions>
        {assigning ? (
          <Typography aria-live="polite" role="status" sx={{ px: 3, pb: 2 }}>
            Atribuindo responsável…
          </Typography>
        ) : null}
      </Dialog>

      <Dialog
        fullWidth
        maxWidth="sm"
        onClose={() => !cancelling && setCancellingCard(null)}
        open={Boolean(cancellingCard)}
      >
        <DialogTitle>Cancelar atendimento</DialogTitle>
        <DialogContent>
          <TextField
            autoFocus
            fullWidth
            label="Motivo do cancelamento"
            multiline
            error={cancellationReason.trim().length > 500}
            helperText={
              cancellationReason.trim().length > 500
                ? "Use no máximo 500 caracteres."
                : undefined
            }
            onChange={(event) => setCancellationReason(event.target.value)}
            slotProps={{ htmlInput: { maxLength: 501 } }}
            sx={{ mt: 1 }}
            value={cancellationReason}
          />
        </DialogContent>
        <DialogActions>
          <Button disabled={cancelling} onClick={() => setCancellingCard(null)}>
            Voltar
          </Button>
          <Button
            color="error"
            disabled={
              !cancellingCard ||
              !cancellationReason.trim() ||
              cancellationReason.trim().length > 500 ||
              cancelling ||
              (cancellingCard ? isCardMutationPending(cancellingCard) : false)
            }
            onClick={() => cancellingCard && void cancel(cancellingCard)}
            variant="contained"
          >
            {cancelling ? "Cancelando…" : "Confirmar cancelamento"}
          </Button>
        </DialogActions>
      </Dialog>
    </CompanyProvider>
  );
}

function columnLabel(column: AtendimentoColumn): string {
  return columns.find((item) => item.id === column)?.label ?? column;
}

function ViewCardDialog({
  card,
  companyName,
  onClose,
}: {
  card: AtendimentoCard | null;
  companyName: string;
  onClose: () => void;
}) {
  return (
    <EntityDialog
      description={`Atendimento de ${companyName}.`}
      onClose={onClose}
      open={Boolean(card)}
      title="Visualizar atendimento"
    >
      {card ? (
        <Stack spacing={1.5}>
          <Typography component="p" variant="subtitle1">
            {card.title}
          </Typography>
          <Typography color="text.secondary">{card.description}</Typography>
          <Stack direction="row" spacing={1} sx={{ flexWrap: "wrap" }}>
            <Chip
              label={columnLabel(card.column)}
              size="small"
              variant="outlined"
            />
            <Chip
              label={
                card.assigneeMembershipId
                  ? "Responsável atribuído"
                  : "Sem responsável"
              }
              size="small"
              variant="outlined"
            />
            <StatusChip
              label={priorityPresentation[card.priority].label}
              tone={priorityPresentation[card.priority].tone}
            />
            <Chip
              label={channelLabels[card.channel]}
              size="small"
              variant="outlined"
            />
            {formatAtendimentoDate(card.slaDueAt) ? (
              <Chip
                label={`Prazo: ${formatAtendimentoDate(card.slaDueAt)}`}
                size="small"
                variant="outlined"
              />
            ) : null}
            {isAtendimentoOverdue(card) ? (
              <StatusChip label="Atrasado" tone="error" />
            ) : null}
          </Stack>
        </Stack>
      ) : null}
    </EntityDialog>
  );
}
