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
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { useEffect, useMemo, useState } from "react";
import { CompanyProvider } from "../../shared/ui/company-context";
import { CompanySelect } from "../../shared/ui/company-select";
import { EntityDialog } from "../../shared/ui/entity-dialog";
import { PageHeader } from "../../shared/ui/page-header";
import { resolveCompanyEntityPermissions } from "../auth/company-capabilities";
import { useSession } from "../auth/session-provider";
import {
  type AtendimentoCard,
  type AtendimentoColumn,
  atendimentoClient,
} from "./atendimento-client";
import { AtendimentoCreateDialog } from "./atendimento-create-dialog";

const columns: readonly { id: AtendimentoColumn; label: string }[] = [
  { id: "novo", label: "Novo" },
  { id: "em_andamento", label: "Em andamento" },
  { id: "concluido", label: "Concluído" },
];

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
  const [assigneeMembershipId, setAssigneeMembershipId] = useState("");
  const [draggedCardId, setDraggedCardId] = useState<string | null>(null);
  const [dropTarget, setDropTarget] = useState<AtendimentoColumn | null>(null);

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

  const canAssign = useMemo(
    () =>
      selectedCompany?.capabilities.some(
        (capability) =>
          capability.name === assignCapability &&
          capability.scopes.includes("Company"),
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
    setViewingCard(null);
    void atendimentoClient.list(companyId).then((result) => {
      if (!active) return;
      if (result)
        setCards(result.filter((card) => card.companyId === companyId));
      else setError("Não foi possível carregar a fila. Tente novamente.");
      setLoading(false);
    });
    return () => {
      active = false;
    };
  }, [companyId]);

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

  async function assign(card: AtendimentoCard) {
    if (!assigneeMembershipId.trim()) return;
    const result = await atendimentoClient.assign(
      companyId,
      card,
      assigneeMembershipId.trim(),
    );
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

  const selectedCompanyName = selectedCompany?.companyName ?? "Sem empresa";

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
          description="Fila operacional por empresa, com cartões e histórico de movimentos. Arraste os cartões entre as colunas para movê-los."
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
            setAssigneeMembershipId("");
            setCompanyId(nextCompanyId);
          }}
          value={companyId}
        />
        {!selectedCompany ? (
          <Alert severity="info">
            Sua sessão não possui empresa com acesso à fila de {title}.
          </Alert>
        ) : null}
        {canAssign ? (
          <TextField
            helperText="Informe o identificador de membership ativo da empresa para atribuir um cartão."
            label="Membership do responsável"
            onChange={(event) => setAssigneeMembershipId(event.target.value)}
            value={assigneeMembershipId}
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
              key={column.id}
              onDragOver={(event) => {
                event.preventDefault();
                event.stopPropagation();
                setDropTarget(column.id);
              }}
              onDragLeave={(event) => {
                event.stopPropagation();
                setDropTarget((current) =>
                  current === column.id ? null : current,
                );
              }}
              onDrop={(event) => {
                event.preventDefault();
                event.stopPropagation();
                handleDrop(column.id);
              }}
              sx={{
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
                        {permissions.canEdit
                          ? columns
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
                        {canAssign ? (
                          <Button
                            disabled={!assigneeMembershipId.trim()}
                            onClick={() => void assign(card)}
                            size="small"
                            startIcon={<PersonAddAltOutlinedIcon />}
                          >
                            Atribuir
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
        onClose={() => setCreateOpen(false)}
        onSubmit={(values) => {
          setCreateOpen(false);
          void createCard(values);
        }}
        open={createOpen}
      />

      <ViewCardDialog
        card={viewingCard}
        companyName={selectedCompanyName}
        onClose={() => setViewingCard(null)}
      />
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
