import AddOutlinedIcon from "@mui/icons-material/AddOutlined";
import AdvanceIcon from "@mui/icons-material/FastForwardOutlined";
import SearchOutlinedIcon from "@mui/icons-material/SearchOutlined";
import VisibilityOutlinedIcon from "@mui/icons-material/VisibilityOutlined";
import {
  Alert,
  Avatar,
  Box,
  Button,
  Card,
  CardActions,
  CardContent,
  Chip,
  CircularProgress,
  Dialog,
  DialogContent,
  DialogTitle,
  List,
  ListItemButton,
  MenuItem,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { CompanyProvider } from "../../shared/ui/company-context";
import { CompanySelect } from "../../shared/ui/company-select";
import { EntityDialog } from "../../shared/ui/entity-dialog";
import { PageHeader } from "../../shared/ui/page-header";
import { resolveCompanyEntityPermissions } from "../auth/company-capabilities";
import { useSession } from "../auth/session-provider";
import {
  type CommandResult,
  type DemandBoardCard,
  type DemandDetail,
  demandasClient,
} from "./demandas-client";

const columns: readonly { id: string; label: string }[] = [
  { id: "initiation", label: "Em iniciação" },
  { id: "execution", label: "Em execução" },
  { id: "awaiting_approval", label: "Aguardando aprovação" },
  { id: "closed", label: "Concluídas" },
];

const readCapability = "demandas.demand.read";
const createCapability = "demandas.demand.create";
const advanceCapability = "demandas.demand.advance";

function messageFor(code: string) {
  if (code === "integrity_conflict")
    return "A demanda foi atualizada por outra pessoa. O quadro foi sincronizado.";
  if (code === "capability_denied" || code === "membership_required")
    return "Sua sessão não tem permissão para esta ação nesta empresa.";
  if (code === "illegal_transition")
    return "Esta etapa avança por outro gatilho (checklist ou tempo).";
  if (code === "resumption_deadline_invalid")
    return "Informe um novo prazo futuro para retomar.";
  return "Não foi possível concluir a ação. Tente novamente.";
}

export function DemandasPage() {
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

  // Contexto ativo por empresa (contrato da baseline de arquitetura): a troca
  // de empresa limpa SINCRONAMENTE cartões/detalhe/erro e invalida respostas
  // tardias da empresa anterior via geração de escopo monotônica — tudo ANTES
  // de commitar o novo companyId, nunca em efeito passivo.
  const scopeGeneration = useRef(0);
  const boardRequest = useRef(0);
  const changeCompany = useCallback(
    (nextCompanyId: string) => {
      scopeGeneration.current += 1;
      boardRequest.current += 1;
      setCards([]);
      setTotal(0);
      setHasMore(false);
      setNextCursor(null);
      setDetail(null);
      setDetailId(null);
      setError(null);
      // O deep link pertence ao contexto anterior (troca explícita): sem
      // limpar ?demand=, o efeito restauraria o id da empresa anterior sob
      // a nova. Na resolução inicial (sem empresa anterior) ele é preservado.
      if (
        companyId &&
        new URLSearchParams(window.location.search).get("demand")
      ) {
        window.history.replaceState(null, "", "/demandas");
      }
      setCompanyId(nextCompanyId);
    },
    [companyId],
  );

  useEffect(() => {
    if (!companies.some((company) => company.companyId === companyId)) {
      changeCompany(companies[0]?.companyId ?? "");
    }
  }, [companies, companyId, changeCompany]);
  const [cards, setCards] = useState<DemandBoardCard[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [onlyOverdue, setOnlyOverdue] = useState(false);
  const [search, setSearch] = useState("");
  const [priority, setPriority] = useState("");
  const [responsible, setResponsible] = useState("");
  const [detailId, setDetailId] = useState<string | null>(null);
  const [detail, setDetail] = useState<DemandDetail | null>(null);

  const selectedCompany = useMemo(
    () => companies.find((company) => company.companyId === companyId),
    [companies, companyId],
  );

  const permissions = useMemo(
    () =>
      resolveCompanyEntityPermissions(selectedCompany, {
        create: createCapability,
        edit: advanceCapability,
      }),
    [selectedCompany],
  );

  const [total, setTotal] = useState(0);
  const [hasMore, setHasMore] = useState(false);
  const [nextCursor, setNextCursor] = useState<string | null>(null);

  const loadBoard = useCallback(
    async (options?: {
      silent?: boolean;
      append?: boolean;
      after?: string;
    }) => {
      if (!companyId) return;
      const generation = scopeGeneration.current;
      const request = ++boardRequest.current;
      if (!options?.silent && !options?.append) setLoading(true);
      const result = await demandasClient.board(companyId, {
        priority,
        responsibleMembershipId: responsible,
        search,
        after: options?.after ?? undefined,
      });
      // Resposta tardia (troca de empresa ou busca mais nova): descartada.
      if (
        generation !== scopeGeneration.current ||
        request !== boardRequest.current
      )
        return;
      if (result) {
        setTotal(result.total);
        setHasMore(result.hasMore);
        setNextCursor(result.nextCursor);
        setCards((current) =>
          options?.append
            ? [
                ...current,
                ...result.items.filter(
                  (incoming) =>
                    !current.some((card) => card.id === incoming.id),
                ),
              ]
            : result.items,
        );
      } else {
        setError("Não foi possível carregar o quadro. Tente novamente.");
      }
      setLoading(false);
    },
    [companyId, priority, responsible, search],
  );

  useEffect(() => {
    void loadBoard();
  }, [loadBoard]);

  // Deep link /demandas?demand=<id>: abre o detalhe direto.
  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const target = params.get("demand");
    if (target && companyId) setDetailId(target);
  }, [companyId]);

  useEffect(() => {
    if (!detailId || !companyId) {
      setDetail(null);
      return;
    }
    const generation = scopeGeneration.current;
    const demandId = detailId;
    void demandasClient.detail(companyId, demandId).then((result) => {
      // Detalhe tardio de outra empresa (ou demanda já fechada): descartado.
      if (generation !== scopeGeneration.current || detailId !== demandId)
        return;
      if (result) setDetail(result);
    });
    window.history.replaceState(null, "", `/demandas?demand=${detailId}`);
  }, [detailId, companyId]);

  function replaceCard(updated?: DemandBoardCard) {
    if (!updated) return void loadBoard({ silent: true });
    setCards((current) =>
      current.map((card) => (card.id === updated.id ? updated : card)),
    );
  }

  async function act(
    card: DemandBoardCard,
    action: () => Promise<CommandResult<DemandBoardCard>>,
  ) {
    const result = await action();
    if (!result.ok) {
      setError(messageFor(result.code));
      const current = result.current;
      if (current)
        setCards((items) =>
          items.map((item) =>
            item.id === card.id ? { ...item, version: current.version } : item,
          ),
        );
      return;
    }
    if (result.demand) replaceCard(result.demand);
    else void loadBoard({ silent: true });
  }

  const visibleCards = onlyOverdue
    ? cards.filter((card) => card.isOverdue)
    : cards;

  return (
    <CompanyProvider company={selectedCompany ?? null}>
      <Stack spacing={3}>
        <PageHeader
          actions={
            permissions.canCreate ? (
              <Button
                component="a"
                href="#criar"
                onClick={(event) => event.preventDefault()}
                startIcon={<AddOutlinedIcon />}
                variant="contained"
                data-testid="demandas-create"
              >
                Nova demanda
              </Button>
            ) : null
          }
          description="Fluxo operacional com gatilhos por conclusão, checklist ou tempo. Avanço sempre por comando explícito — sem arrastar cartões."
          title="Demandas"
        />
        {error ? (
          <Alert onClose={() => setError(null)} severity="error">
            {error}
          </Alert>
        ) : null}
        <CompanySelect
          companies={companies}
          label="Empresa"
          onChange={changeCompany}
          value={companyId}
        />
        {!selectedCompany ? (
          <Alert severity="info">
            Sua sessão não possui empresa com acesso ao quadro de Demandas.
          </Alert>
        ) : null}
        <Stack direction={{ sm: "row", xs: "column" }} spacing={1.5}>
          <TextField
            data-testid="demandas-search"
            label="Buscar título"
            onChange={(event) => setSearch(event.target.value)}
            size="small"
            value={search}
          />
          <TextField
            data-testid="demandas-priority"
            label="Prioridade"
            onChange={(event) => setPriority(event.target.value)}
            select
            slotProps={{ select: { displayEmpty: true } }}
            size="small"
            value={priority}
          >
            <MenuItem value="">Todas</MenuItem>
            <MenuItem value="critical">Crítica</MenuItem>
            <MenuItem value="high">Alta</MenuItem>
            <MenuItem value="medium">Média</MenuItem>
            <MenuItem value="low">Baixa</MenuItem>
          </TextField>
          <TextField
            data-testid="demandas-responsible"
            helperText="Filtro server-side por membership."
            label="Responsável"
            onChange={(event) => setResponsible(event.target.value)}
            size="small"
            value={responsible}
          />
          <Button
            color={onlyOverdue ? "primary" : "inherit"}
            onClick={() => setOnlyOverdue((current) => !current)}
            startIcon={<SearchOutlinedIcon />}
            variant={onlyOverdue ? "contained" : "outlined"}
            data-testid="demandas-overdue-view"
          >
            Atrasadas
          </Button>
        </Stack>
        {loading ? (
          <CircularProgress
            aria-label="Carregando quadro"
            size={24}
            role="status"
          />
        ) : null}
        <Box
          sx={{
            display: "grid",
            gap: 2,
            gridTemplateColumns: { md: "repeat(4, 1fr)", xs: "1fr" },
          }}
        >
          {columns.map((column) => (
            <Box key={column.id}>
              <Typography
                color="text.secondary"
                component="h2"
                sx={{ mb: 1, textTransform: "uppercase" }}
                variant="overline"
              >
                {column.label}
              </Typography>
              <Stack spacing={1.25}>
                {visibleCards
                  .filter((card) => card.stage === column.id)
                  .map((card) => (
                    <DemandCard
                      card={card}
                      key={card.id}
                      onAdvance={() =>
                        void act(card, () =>
                          demandasClient.advance(companyId, card),
                        )
                      }
                      onOpen={() => setDetailId(card.id)}
                    />
                  ))}
                {visibleCards.filter((card) => card.stage === column.id)
                  .length === 0 ? (
                  <Typography color="text.secondary" variant="body2">
                    Nenhuma demanda.
                  </Typography>
                ) : null}
              </Stack>
            </Box>
          ))}
        </Box>
        {hasMore ? (
          <Button
            data-testid="demandas-load-more"
            disabled={loading}
            onClick={() =>
              void loadBoard({
                silent: true,
                append: true,
                after: nextCursor ?? undefined,
              })
            }
            variant="outlined"
          >
            Carregar mais ({cards.length}/{total})
          </Button>
        ) : null}
      </Stack>

      <DemandDetailDialog
        detail={detail}
        onClose={() => setDetailId(null)}
        open={Boolean(detailId)}
      />
    </CompanyProvider>
  );
}

function hoursInStage(stageStartedAt: string): string {
  const hours = Math.max(
    0,
    Math.floor((Date.now() - Date.parse(stageStartedAt)) / 3_600_000),
  );
  return `${hours}h na etapa`;
}

function deadlineLabel(
  effectiveDeadline: string | null,
  isOverdue: boolean,
): string | null {
  if (!effectiveDeadline) return null;
  const deltaMs = Date.parse(effectiveDeadline) - Date.now();
  const hours = Math.round(Math.abs(deltaMs) / 3_600_000);
  if (isOverdue) return `atrasada há ${hours}h`;
  return `vence em ${hours}h`;
}

function DemandCard({
  card,
  onAdvance,
  onOpen,
}: {
  card: DemandBoardCard;
  onAdvance: () => void;
  onOpen: () => void;
}) {
  return (
    <Card variant="outlined" data-testid={`demand-card-${card.id}`}>
      <CardContent sx={{ pb: 1 }}>
        <Stack direction="row" spacing={1}>
          <Avatar sx={{ width: 28, height: 28 }}>
            {card.responsibleMembershipId.slice(0, 2).toUpperCase()}
          </Avatar>
          <Box sx={{ flex: 1, minWidth: 0 }}>
            <Typography variant="subtitle1">{card.title}</Typography>
            <Typography color="text.secondary" variant="caption">
              {card.demandDisplayNumber}
            </Typography>
          </Box>
        </Stack>
        <Stack direction="row" spacing={0.75} sx={{ flexWrap: "wrap", mt: 1 }}>
          <Chip
            label={priorityLabel(card.priority)}
            size="small"
            variant="outlined"
          />
          {card.isOverdue ? (
            <Chip
              color="error"
              data-testid="overdue-chip"
              label="Atrasada"
              size="small"
            />
          ) : null}
          {deadlineLabel(card.effectiveDeadline, card.isOverdue) ? (
            <Chip
              color={card.isOverdue ? "error" : "default"}
              label={deadlineLabel(card.effectiveDeadline, card.isOverdue)}
              size="small"
              variant="outlined"
            />
          ) : null}
          <Chip
            label={hoursInStage(card.stageStartedAt)}
            size="small"
            variant="outlined"
          />
          {card.automationPaused ? (
            <Chip color="warning" label="Pausada" size="small" />
          ) : null}
        </Stack>
      </CardContent>
      <CardActions>
        <Button
          onClick={onOpen}
          size="small"
          startIcon={<VisibilityOutlinedIcon />}
        >
          Detalhes
        </Button>
        {card.stage !== "closed" ? (
          <Button
            data-testid={`advance-${card.id}`}
            onClick={onAdvance}
            size="small"
            startIcon={<AdvanceIcon />}
          >
            Avançar
          </Button>
        ) : null}
      </CardActions>
    </Card>
  );
}

function priorityLabel(priority: string): string {
  switch (priority) {
    case "critical":
      return "Crítica";
    case "high":
      return "Alta";
    case "low":
      return "Baixa";
    default:
      return "Média";
  }
}

export function DemandDetailDialog({
  detail,
  onClose,
  open,
}: {
  detail: DemandDetail | null;
  onClose: () => void;
  open: boolean;
}) {
  return (
    <Dialog fullWidth maxWidth="sm" onClose={onClose} open={open}>
      <DialogTitle>{detail?.demand.title ?? "Detalhe da demanda"}</DialogTitle>
      <DialogContent dividers>
        {!detail ? (
          <CircularProgress
            aria-label="Carregando detalhe"
            size={24}
            role="status"
          />
        ) : (
          <Stack spacing={2}>
            <Typography color="text.secondary">
              {detail.demand.description}
            </Typography>
            <Stack direction="row" spacing={0.75} sx={{ flexWrap: "wrap" }}>
              <Chip
                label={priorityLabel(detail.demand.priority)}
                size="small"
                variant="outlined"
              />
              <Chip
                label={`Ciclo ${detail.demand.cycleCount}`}
                size="small"
                variant="outlined"
              />
              {detail.demand.isOverdue ? (
                <Chip color="error" label="Atrasada" size="small" />
              ) : null}
            </Stack>
            <Box data-testid="demand-checklist">
              <Typography component="h3" variant="overline">
                Checklist
              </Typography>
              <List dense disablePadding>
                {detail.checklist.map((item) => (
                  <ListItemButton disabled={item.isDone} key={item.id}>
                    <span aria-hidden>{item.isDone ? "✓" : "•"}</span>
                    <span>{item.text}</span>
                  </ListItemButton>
                ))}
              </List>
            </Box>
            <Box data-testid="demand-history">
              <Typography component="h3" variant="overline">
                Histórico
              </Typography>
              <List dense disablePadding>
                {detail.history.slice(0, 12).map((entry) => (
                  <ListItemButton key={entry.id} divider>
                    {entry.kind}
                    {entry.toStage ? ` → ${entry.toStage}` : ""} · ciclo{" "}
                    {entry.cycleCount}
                  </ListItemButton>
                ))}
              </List>
            </Box>
            <Box data-testid="demand-participants">
              <Typography component="h3" variant="overline">
                Participantes
              </Typography>
              <Stack direction="row" spacing={0.75} sx={{ flexWrap: "wrap" }}>
                {detail.participants.map((participant) => (
                  <Chip
                    key={participant.id}
                    label={`${participant.role}: ${participant.userId.slice(0, 6)}…`}
                    size="small"
                    variant="outlined"
                  />
                ))}
              </Stack>
            </Box>
          </Stack>
        )}
      </DialogContent>
    </Dialog>
  );
}

// EntityDialog importado para manter a padronização visual de diálogos futuros
// (criação/edição completa entram na fatia de web de rotinas).
void EntityDialog;
