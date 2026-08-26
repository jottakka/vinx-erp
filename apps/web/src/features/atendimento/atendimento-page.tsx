import AddOutlinedIcon from "@mui/icons-material/AddOutlined";
import DragIndicatorOutlinedIcon from "@mui/icons-material/DragIndicatorOutlined";
import PersonAddAltOutlinedIcon from "@mui/icons-material/PersonAddAltOutlined";
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
  MenuItem,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { useEffect, useMemo, useRef, useState } from "react";
import { CompanyProvider } from "../../shared/ui/company-context";
import { CompanySelect } from "../../shared/ui/company-select";
import { EntityDialog } from "../../shared/ui/entity-dialog";
import { PageHeader } from "../../shared/ui/page-header";
import { resolveCompanyEntityPermissions } from "../auth/company-capabilities";
import { useSession } from "../auth/session-provider";
import {
  type AtendimentoAssignmentOption,
  type AtendimentoCard,
  type AtendimentoColumn,
  atendimentoClient,
} from "./atendimento-client";
import { AtendimentoCreateDialog } from "./atendimento-create-dialog";

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

function messageFor(code: string) {
  if (code === "integrity_conflict")
    return "O cartão foi atualizado por outra pessoa. A fila foi sincronizada.";
  if (code === "capability_denied" || code === "membership_required")
    return "Sua sessão não tem permissão para esta ação nesta empresa.";
  return "Não foi possível concluir a ação. Tente novamente.";
}

export function AtendimentoPage({ title = "Atendimento" }: { title?: string }) {
  const { bootstrap } = useSession();
  const companies = useMemo(
    () =>
      (bootstrap?.companies ?? []).filter((company) =>
        company.capabilities.some(
          (capability) =>
            capability.name === readCapability &&
            capability.scopes.includes("Company"),
        ),
      ),
    [bootstrap],
  );
  const [companyId, setCompanyId] = useState("");
  const [cards, setCards] = useState<AtendimentoCard[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [createOpen, setCreateOpen] = useState(false);
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
  const [cancellingCard, setCancellingCard] = useState<AtendimentoCard | null>(
    null,
  );
  const [cancellationReason, setCancellationReason] = useState("");
  const [cancelling, setCancelling] = useState(false);
  const [draggedCardId, setDraggedCardId] = useState<string | null>(null);
  const [dropTarget, setDropTarget] = useState<AtendimentoColumn | null>(null);
  const assignmentEpoch = useRef(0);
  const assignmentAbort = useRef<AbortController | null>(null);

  const selectedCompany = useMemo(
    () => companies.find((company) => company.companyId === companyId),
    [companies, companyId],
  );

  const permissions = useMemo(
    () =>
      resolveCompanyEntityPermissions(selectedCompany, {
        create: createCapability,
        edit: moveCapability,
      }),
    [selectedCompany],
  );

  useEffect(() => {
    if (!companies.some((company) => company.companyId === companyId)) {
      setCompanyId(companies[0]?.companyId ?? "");
    }
  }, [companies, companyId]);

  useEffect(() => {
    if (!companies.length) {
      setCards([]);
      return;
    }
    let active = true;
    setCards([]);
    setLoading(true);
    setError(null);
    setViewingCard(null);
    void Promise.all(
      companies.map(async (company) => ({
        companyId: company.companyId,
        cards: await atendimentoClient.list(company.companyId),
      })),
    ).then((results) => {
      if (!active) return;
      if (results.every((result) => result.cards !== null)) {
        setCards(
          results.flatMap(
            (result) =>
              result.cards?.filter(
                (card) => card.companyId === result.companyId,
              ) ?? [],
          ),
        );
      } else setError("Não foi possível carregar a fila. Tente novamente.");
      setLoading(false);
    });
    return () => {
      active = false;
    };
  }, [companies]);

  useEffect(() => {
    const controller = new AbortController();
    setCreateOptions([]);
    if (!companyId || !permissions.canCreate) return () => controller.abort();
    void atendimentoClient
      .assignmentOptions(companyId, controller.signal)
      .then((options) => {
        if (!controller.signal.aborted && options) setCreateOptions(options);
      });
    return () => controller.abort();
  }, [companyId, permissions.canCreate]);

  async function createCard(values: {
    title: string;
    description: string;
    attachmentFileId?: string;
    participantMembershipIds: readonly string[];
  }) {
    if (!companyId || loading) return;
    const result =
      values.attachmentFileId || values.participantMembershipIds.length
        ? await atendimentoClient.create(
            companyId,
            values.title,
            values.description,
            {
              attachmentFileId: values.attachmentFileId,
              participantMembershipIds: values.participantMembershipIds,
            },
          )
        : await atendimentoClient.create(
            companyId,
            values.title,
            values.description,
          );
    if (!result.ok) {
      setError(messageFor(result.code));
      return;
    }
    setCards((current) => [...current, result.card]);
  }

  async function move(card: AtendimentoCard, column: AtendimentoColumn) {
    if (card.column === column || card.column === "cancelado") return;
    const result = await atendimentoClient.move(card.companyId, card, column);
    if (!result.ok) return recover(result.code, result.current);
    setCards((current) =>
      current.map((item) => (item.id === card.id ? result.card : item)),
    );
  }

  async function assign(card: AtendimentoCard) {
    if (!assigneeMembershipId.trim()) return;
    const result = await atendimentoClient.assign(
      card.companyId,
      card,
      assigneeMembershipId.trim(),
    );
    if (!result.ok) return recover(result.code, result.current);
    setCards((current) =>
      current.map((item) => (item.id === card.id ? result.card : item)),
    );
    setAssigningCard(null);
    setAssigneeMembershipId("");
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
    if (!reason || reason.length > 500 || cancelling) return;
    setCancelling(true);
    const result = await atendimentoClient.cancel(card.companyId, card, reason);
    if (!result.ok) {
      setCancelling(false);
      return recover(result.code, result.current);
    }
    setCards((current) =>
      current.map((item) => (item.id === card.id ? result.card : item)),
    );
    setCancellingCard(null);
    setCancellationReason("");
    setCancelling(false);
  }

  function recover(code: string, current?: AtendimentoCard) {
    if (current)
      setCards((cards) =>
        cards.map((card) => (card.id === current.id ? current : card)),
      );
    setError(messageFor(code));
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

  return (
    <CompanyProvider company={selectedCompany ?? null}>
      <Stack spacing={3}>
        <PageHeader
          actions={
            permissions.canCreate ? (
              <Button
                onClick={() => setCreateOpen(true)}
                startIcon={<AddOutlinedIcon />}
                variant="contained"
              >
                Novo cartão
              </Button>
            ) : null
          }
          description="Fila operacional das empresas autorizadas. Os cartões preservam a empresa de origem e só comandos explícitos alteram o estado."
          title={title}
        />
        {error ? (
          <Alert onClose={() => setError(null)} severity="error">
            {error}
          </Alert>
        ) : null}
        <CompanySelect
          companies={companies}
          onChange={(nextCompanyId) => {
            setAssigneeMembershipId("");
            setCompanyId(nextCompanyId);
          }}
          label="Empresa para novo cartão"
          value={companyId}
        />
        {!selectedCompany ? (
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
                {cards
                  .filter((card) => card.column === column.id)
                  .map((card) => (
                    <Card
                      key={card.id}
                      draggable={
                        canEditCard(card) && card.column !== "cancelado"
                      }
                      onDragEnd={() => setDraggedCardId(null)}
                      onDragStart={() => setDraggedCardId(card.id)}
                      sx={{
                        cursor:
                          canEditCard(card) && card.column !== "cancelado"
                            ? "grab"
                            : "default",
                        "&:active": {
                          cursor:
                            canEditCard(card) && card.column !== "cancelado"
                              ? "grabbing"
                              : "default",
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
                            <Chip
                              label={
                                companies.find(
                                  (company) =>
                                    company.companyId === card.companyId,
                                )?.companyName ?? "Empresa autorizada"
                              }
                              size="small"
                              sx={{ mt: 1 }}
                              variant="outlined"
                            />
                          </Box>
                          {canEditCard(card) && card.column !== "cancelado" ? (
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
                        </Stack>
                      </CardContent>
                      <CardActions>
                        <Button
                          onClick={() => setViewingCard(card)}
                          size="small"
                          startIcon={<VisibilityOutlinedIcon />}
                        >
                          Visualizar
                        </Button>
                        {canEditCard(card) && card.column !== "cancelado"
                          ? workflowColumns
                              .filter(
                                (destination) => destination.id !== card.column,
                              )
                              .map((destination) => (
                                <Button
                                  key={destination.id}
                                  onClick={() =>
                                    void move(card, destination.id)
                                  }
                                  size="small"
                                >
                                  Mover para {destination.label}
                                </Button>
                              ))
                          : null}
                        {canAssignCard(card) && card.column !== "cancelado" ? (
                          <Button
                            onClick={() => void openAssignment(card)}
                            size="small"
                            startIcon={<PersonAddAltOutlinedIcon />}
                          >
                            Atribuir
                          </Button>
                        ) : null}
                        {canEditCard(card) && card.column !== "cancelado" ? (
                          <Button
                            color="error"
                            onClick={() => setCancellingCard(card)}
                            size="small"
                          >
                            Cancelar cartão
                          </Button>
                        ) : null}
                      </CardActions>
                    </Card>
                  ))}
                {cards.filter((card) => card.column === column.id).length ===
                0 ? (
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
        companyId={companyId}
        onClose={() => setCreateOpen(false)}
        onSubmit={(values) => {
          return createCard(values);
        }}
        open={createOpen}
      />

      <ViewCardDialog
        card={viewingCard}
        companyName={
          companies.find(
            (company) => company.companyId === viewingCard?.companyId,
          )?.companyName ?? "Empresa autorizada"
        }
        onClose={() => setViewingCard(null)}
      />

      <Dialog
        onClose={() => {
          assignmentEpoch.current++;
          assignmentAbort.current?.abort();
          setAssigningCard(null);
        }}
        open={Boolean(assigningCard)}
      >
        <DialogTitle>Atribuir responsável</DialogTitle>
        <DialogContent>
          <TextField
            fullWidth
            label="Responsável"
            onChange={(event) => setAssigneeMembershipId(event.target.value)}
            select
            sx={{ mt: 1, minWidth: 280 }}
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
            onClick={() => {
              assignmentEpoch.current++;
              assignmentAbort.current?.abort();
              setAssigningCard(null);
            }}
          >
            Cancelar
          </Button>
          <Button
            disabled={!assigningCard || !assigneeMembershipId}
            onClick={() => assigningCard && void assign(assigningCard)}
            variant="contained"
          >
            Confirmar atribuição
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog
        onClose={() => !cancelling && setCancellingCard(null)}
        open={Boolean(cancellingCard)}
      >
        <DialogTitle>Cancelar cartão</DialogTitle>
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
            sx={{ mt: 1, minWidth: 320 }}
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
              cancelling
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
      description={`Cartão de ${companyName}. Apenas leitura.`}
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
          </Stack>
          <Typography color="text.secondary" variant="caption">
            Versão {card.version}
          </Typography>
        </Stack>
      ) : null}
    </EntityDialog>
  );
}
