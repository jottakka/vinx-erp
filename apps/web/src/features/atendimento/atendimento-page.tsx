import AddOutlinedIcon from "@mui/icons-material/AddOutlined";
import DragIndicatorOutlinedIcon from "@mui/icons-material/DragIndicatorOutlined";
import VisibilityOutlinedIcon from "@mui/icons-material/VisibilityOutlined";
import {
  Alert,
  Box,
  Button,
  Card,
  CardActions,
  CardContent,
  Chip,
  Stack,
  Typography,
} from "@mui/material";
import { useCallback, useEffect, useMemo, useState } from "react";
import { CompanyProvider } from "../../shared/ui/company-context";
import { CompanySelect } from "../../shared/ui/company-select";
import { PageHeader } from "../../shared/ui/page-header";
import { resolveCompanyEntityPermissions } from "../auth/company-capabilities";
import { useSession } from "../auth/session-provider";
import {
  type AtendimentoCard,
  type AtendimentoColumn,
  type LookupsResponse,
  type QueueFilter,
  atendimentoClient,
} from "./atendimento-client";
import { AtendimentoCreateDialog } from "./atendimento-create-dialog";
import {
  AtendimentoDetailDialog,
} from "./atendimento-detail-dialog";
import {
  AtendimentoFilterBar,
  filterFromSearchParams,
  replaceUrlWithFilter,
} from "./atendimento-filter-bar";
import {
  channelLabel,
  columnLabels,
  priorityColor,
  priorityLabel,
} from "./atendimento-lookup";
import { CategoriesAdminSection } from "./atendimento-categories-admin";

const readCapability = "service_desk.ticket.read_queue";
const createCapability = "service_desk.ticket.create";
const moveCapability = "service_desk.ticket.move";
const assignCapability = "service_desk.ticket.assign";
const lookupAdminCapability = "service_desk.lookup.admin";

const columns: readonly AtendimentoColumn[] = [
  "novo",
  "em_andamento",
  "concluido",
];

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
  const [viewingCardId, setViewingCardId] = useState<string | null>(null);
  const [draggedCardId, setDraggedCardId] = useState<string | null>(null);
  const [dropTarget, setDropTarget] = useState<AtendimentoColumn | null>(null);
  // Filtros nascem da URL (compartilháveis por link) e reescrevem a URL.
  const [filter, setFilter] = useState<QueueFilter>(() =>
    filterFromSearchParams(window.location.search),
  );
  const [lookups, setLookups] = useState<LookupsResponse | null>(null);

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

  const hasCapability = useCallback(
    (name: string) =>
      selectedCompany?.capabilities.some(
        (capability) =>
          capability.name === name && capability.scopes.includes("Company"),
      ) ?? false,
    [selectedCompany],
  );

  useEffect(() => {
    if (!companies.some((company) => company.companyId === companyId)) {
      setCompanyId(companies[0]?.companyId ?? "");
    }
  }, [companies, companyId]);

  useEffect(() => {
    if (!companyId) return;
    let active = true;
    setCards([]);
    setLoading(true);
    setError(null);
    setViewingCardId(null);
    void atendimentoClient.list(companyId, filter).then((result) => {
      if (!active) return;
      if (result)
        setCards(result.filter((card) => card.companyId === companyId));
      else setError("Não foi possível carregar a fila. Tente novamente.");
      setLoading(false);
    });
    return () => {
      active = false;
    };
  }, [companyId, filter]);

  useEffect(() => {
    if (!companyId) return;
    void atendimentoClient.lookups(companyId).then((result) => {
      if (result) setLookups(result);
    });
  }, [companyId]);

  function applyFilter(next: QueueFilter) {
    replaceUrlWithFilter(next);
    setFilter(next);
  }

  async function createCard(values: { title: string; description: string }) {
    if (!companyId) return;
    const result = await atendimentoClient.create(
      companyId,
      values.title,
      values.description,
    );
    if (!result.ok) return setError(messageFor(result.code));
    setCards((current) => [...current, result.card]);
  }

  async function move(card: AtendimentoCard, column: AtendimentoColumn) {
    if (card.column === column) return;
    const result = await atendimentoClient.move(companyId, card, column);
    if (!result.ok) return recover(result.code, result.current);
    setCards((current) =>
      current.map((item) => (item.id === card.id ? result.card : item)),
    );
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
    if (card && card.column !== column) void move(card, column);
    setDraggedCardId(null);
    setDropTarget(null);
  }

  const viewingCard =
    cards.find((card) => card.id === viewingCardId) ?? null;
  const categoryNames = new Map(
    (lookups?.categories ?? []).map((category) => [category.id, category.name]),
  );
  const channelKeys = new Map(
    (lookups?.channels ?? []).map((channel) => [channel.id, channel.key]),
  );

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
          description="Fila operacional por empresa com filtros compartilháveis e histórico. Arraste os cartões entre as colunas para movê-los."
          title={title}
        />
        {error ? (
          <Alert onClose={() => setError(null)} severity="error">
            {error}
          </Alert>
        ) : null}
        <CompanySelect
          companies={companies}
          label="Empresa"
          onChange={(nextCompanyId) => {
            setLookups(null);
            setCompanyId(nextCompanyId);
          }}
          value={companyId}
        />
        {!selectedCompany ? (
          <Alert severity="info">
            Sua sessão não possui empresa com acesso à fila de {title}.
          </Alert>
        ) : null}
        {companyId ? (
          <AtendimentoFilterBar
            companyId={companyId}
            disabled={loading}
            filter={filter}
            onChange={applyFilter}
          />
        ) : null}
        {hasCapability(lookupAdminCapability) ? (
          <CategoriesAdminSection
            categories={lookups?.categories ?? []}
            companyId={companyId}
            onChanged={() => {
              void atendimentoClient.lookups(companyId).then(setLookups);
            }}
            onError={setError}
          />
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
            gridTemplateColumns: { md: "repeat(3, 1fr)", xs: "1fr" },
          }}
        >
          {columns.map((column) => (
            <Box
              key={column}
              onDragOver={(event) => {
                event.preventDefault();
                event.stopPropagation();
                setDropTarget(column);
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
                  current === column ? null : current,
                );
              }}
              onDrop={(event) => {
                event.preventDefault();
                event.stopPropagation();
                handleDrop(column);
              }}
              sx={{
                bgcolor:
                  dropTarget === column
                    ? "action.selected"
                    : "background.paper",
                border: 1,
                borderColor:
                  dropTarget === column ? "primary.main" : "divider",
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
            >
              <Typography
                color="text.secondary"
                component="h2"
                sx={{ mb: 1.5, textTransform: "uppercase" }}
                variant="overline"
              >
                {columnLabels[column]}
              </Typography>
              <Stack spacing={1.25}>
                {cards
                  .filter((card) => card.column === column)
                  .map((card) => (
                    <Card
                      key={card.id}
                      draggable={permissions.canEdit}
                      onDragEnd={() => setDraggedCardId(null)}
                      onDragStart={() => setDraggedCardId(card.id)}
                      sx={{
                        cursor: permissions.canEdit ? "grab" : "default",
                        "&:active": {
                          cursor: permissions.canEdit ? "grabbing" : "default",
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
                          </Box>
                          {permissions.canEdit ? (
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
                            label={columnLabels[card.column]}
                            size="small"
                            variant="outlined"
                          />
                          {card.priority ? (
                            <Chip
                              color={priorityColor(card.priority)}
                              label={priorityLabel(card.priority)}
                              size="small"
                            />
                          ) : null}
                          {card.overdue ? (
                            <Chip
                              color="error"
                              label="Atrasado"
                              size="small"
                            />
                          ) : null}
                          {card.categoryRefId ? (
                            <Chip
                              label={
                                categoryNames.get(card.categoryRefId) ??
                                "Categoria"
                              }
                              size="small"
                              variant="outlined"
                            />
                          ) : null}
                          {card.channelRefId ? (
                            <Chip
                              label={
                                channelKeys.get(card.channelRefId) === undefined
                                  ? "Canal"
                                  : (channelLabel(channelKeys.get(card.channelRefId) as string))
                              }
                              size="small"
                              variant="outlined"
                            />
                          ) : null}
                        </Stack>
                      </CardContent>
                      <CardActions>
                        <Button
                          onClick={() => setViewingCardId(card.id)}
                          size="small"
                          startIcon={<VisibilityOutlinedIcon />}
                        >
                          Visualizar
                        </Button>
                        {permissions.canEdit
                          ? columns
                              .filter((destination) => destination !== card.column)
                              .map((destination) => (
                                <Button
                                  key={destination}
                                  onClick={() => void move(card, destination)}
                                  size="small"
                                >
                                  Mover para {columnLabels[destination]}
                                </Button>
                              ))
                          : null}
                      </CardActions>
                    </Card>
                  ))}
                {cards.filter((card) => card.column === column).length ===
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
        onClose={() => setCreateOpen(false)}
        onSubmit={(values) => {
          setCreateOpen(false);
          void createCard(values);
        }}
        open={createOpen}
      />

      {companyId ? (
        <AtendimentoDetailDialog
          canAssign={hasCapability(assignCapability)}
          card={viewingCard}
          categories={lookups?.categories ?? []}
          channels={lookups?.channels ?? []}
          companyId={companyId}
          onClose={() => setViewingCardId(null)}
          onUpdated={(next) =>
            setCards((current) =>
              current.map((card) => (card.id === next.id ? next : card)),
            )
          }
        />
      ) : null}
    </CompanyProvider>
  );
}
