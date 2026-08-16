import {
  Alert,
  Button,
  Card,
  CardActions,
  CardContent,
  FormControl,
  Grid,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { useEffect, useMemo, useState } from "react";
import { useSession } from "../auth/session-provider";
import {
  type AtendimentoCard,
  type AtendimentoColumn,
  atendimentoClient,
} from "./atendimento-client";

const columns: readonly { id: AtendimentoColumn; label: string }[] = [
  { id: "novo", label: "Novo" },
  { id: "em_andamento", label: "Em andamento" },
  { id: "concluido", label: "Concluído" },
];

export function AtendimentoPage({ title = "Atendimento" }: { title?: string }) {
  const { bootstrap } = useSession();
  const companies = bootstrap?.companies ?? [];
  const [companyId, setCompanyId] = useState("");
  const [cards, setCards] = useState<AtendimentoCard[]>([]);
  const [description, setDescription] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [titleValue, setTitleValue] = useState("");
  const [assigneeMembershipId, setAssigneeMembershipId] = useState("");

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

  const selectedCompany = useMemo(
    () => companies.find((company) => company.companyId === companyId),
    [companies, companyId],
  );

  async function createCard() {
    if (!companyId || !titleValue.trim() || !description.trim()) return;
    const result = await atendimentoClient.create(
      companyId,
      titleValue,
      description,
    );
    if (!result.ok) return setError(messageFor(result.code));
    setCards((current) => [...current, result.card]);
    setTitleValue("");
    setDescription("");
  }

  async function move(card: AtendimentoCard, column: AtendimentoColumn) {
    const result = await atendimentoClient.move(companyId, card, column);
    if (!result.ok) return recover(result.code, result.current);
    setCards((current) =>
      current.map((item) => (item.id === card.id ? result.card : item)),
    );
  }

  async function assign(card: AtendimentoCard) {
    if (!assigneeMembershipId) return;
    const result = await atendimentoClient.assign(
      companyId,
      card,
      assigneeMembershipId,
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

  return (
    <Stack spacing={3}>
      <div>
        <Typography component="h1" variant="h4">
          {title}
        </Typography>
        <Typography color="text.secondary">
          Fila operacional por empresa, com cartões fixos e histórico de
          movimentos.
        </Typography>
      </div>
      {error ? (
        <Alert onClose={() => setError(null)} severity="error">
          {error}
        </Alert>
      ) : null}
      <FormControl disabled={!companies.length} fullWidth>
        <InputLabel id="atendimento-company-label">Empresa</InputLabel>
        <Select
          label="Empresa"
          labelId="atendimento-company-label"
          onChange={(event) => setCompanyId(event.target.value)}
          value={companyId}
        >
          {companies.map((company) => (
            <MenuItem key={company.companyId} value={company.companyId}>
              {company.companyName}
            </MenuItem>
          ))}
        </Select>
      </FormControl>
      {!selectedCompany ? (
        <Alert severity="info">
          Sua sessão não possui empresa disponível para Atendimento.
        </Alert>
      ) : null}
      {selectedCompany ? (
        <Card variant="outlined">
          <CardContent>
            <Stack spacing={2}>
              <Typography variant="h6">Novo atendimento</Typography>
              <TextField
                label="Título"
                onChange={(event) => setTitleValue(event.target.value)}
                value={titleValue}
              />
              <TextField
                label="Descrição"
                minRows={3}
                multiline
                onChange={(event) => setDescription(event.target.value)}
                value={description}
              />
              <Button
                disabled={!titleValue.trim() || !description.trim()}
                onClick={() => void createCard()}
                variant="contained"
              >
                Criar cartão
              </Button>
            </Stack>
          </CardContent>
        </Card>
      ) : null}
      <TextField
        helperText="Informe o identificador de membership ativo da empresa para atribuir um cartão."
        label="Membership do responsável"
        onChange={(event) => setAssigneeMembershipId(event.target.value)}
        value={assigneeMembershipId}
      />
      {loading ? (
        <Typography aria-live="polite" role="status">
          Carregando fila…
        </Typography>
      ) : null}
      <Grid container spacing={2}>
        {columns.map((column) => (
          <Grid key={column.id} size={{ md: 4, xs: 12 }}>
            <Stack spacing={1}>
              <Typography component="h2" variant="h6">
                {column.label}
              </Typography>
              {cards
                .filter((card) => card.column === column.id)
                .map((card) => (
                  <Card key={card.id} variant="outlined">
                    <CardContent>
                      <Typography variant="subtitle1">{card.title}</Typography>
                      <Typography color="text.secondary">
                        {card.description}
                      </Typography>
                    </CardContent>
                    <CardActions>
                      {columns
                        .filter((destination) => destination.id !== card.column)
                        .map((destination) => (
                          <Button
                            key={destination.id}
                            onClick={() => void move(card, destination.id)}
                            size="small"
                          >
                            Mover para {destination.label}
                          </Button>
                        ))}
                      <Button
                        disabled={!assigneeMembershipId}
                        onClick={() => void assign(card)}
                        size="small"
                      >
                        Atribuir responsável
                      </Button>
                    </CardActions>
                  </Card>
                ))}
            </Stack>
          </Grid>
        ))}
      </Grid>
    </Stack>
  );
}

function messageFor(code: string) {
  if (code === "integrity_conflict")
    return "O cartão foi atualizado por outra pessoa. A fila foi sincronizada.";
  if (code === "capability_denied" || code === "membership_required")
    return "Sua sessão não tem permissão para esta ação nesta empresa.";
  return "Não foi possível concluir a ação. Tente novamente.";
}
