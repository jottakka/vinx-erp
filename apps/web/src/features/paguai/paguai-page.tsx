import {
  Alert,
  Box,
  Button,
  Card,
  CardActionArea,
  CardContent,
  MenuItem,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import {
  type FormEvent,
  useCallback,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
} from "react";
import { PageHeader } from "../../shared/ui/page-header";
import { useSession } from "../auth/session-provider";
import {
  createPaguaiDraftClient,
  type PaguaiDraft,
  type PaguaiDraftError,
} from "./paguai-draft-client";

const client = createPaguaiDraftClient();
const capabilityNames = ["paguai.draft.read", "paguai.draft.create"] as const;
type PaguaiDraftClient = ReturnType<typeof createPaguaiDraftClient>;

function hasCompanyCapability(
  company: {
    capabilities: readonly { name: string; scopes: readonly string[] }[];
  },
  name: string,
) {
  return company.capabilities.some(
    (capability) =>
      capability.name === name && capability.scopes.includes("Company"),
  );
}

function message(error: PaguaiDraftError) {
  return (
    {
      400: "Revise os campos obrigatórios.",
      401: "Sua sessão expirou.",
      403: "Você não tem acesso a esta empresa.",
      404: "Solicitação não encontrada.",
      409: "Este identificador já foi usado com outros dados.",
      429: "Muitas tentativas. Aguarde antes de tentar novamente.",
      503: "Paguai está indisponível. Tente novamente com o mesmo identificador.",
      network:
        "Não foi possível alcançar o servidor. Tente novamente com o mesmo identificador.",
      protocol: "A resposta do servidor não pôde ser validada.",
    } as const
  )[error];
}

const maximumAmountCents = 999_999_999_999_999_999n;

function amountFromReais(value: string): string | null {
  const trimmed = value.trim();
  let normalized: string;
  if (/^\d+(?:,\d{1,2})$/.test(trimmed)) {
    normalized = trimmed.replace(",", ".");
  } else if (/^\d{1,3}(?:\.\d{3})+,\d{1,2}$/.test(trimmed)) {
    normalized = trimmed.replace(/\./g, "").replace(",", ".");
  } else if (/^\d+(?:\.\d{1,2})$/.test(trimmed)) {
    normalized = trimmed;
  } else if (/^\d{1,3}(?:\.\d{3})+$/.test(trimmed)) {
    normalized = trimmed.replace(/\./g, "");
  } else if (/^\d+$/.test(trimmed)) {
    normalized = `${trimmed}.00`;
  } else {
    return null;
  }
  if (!/^\d+(?:\.\d{1,2})?$/.test(normalized)) return null;
  const [whole, fraction = ""] = normalized.split(".");
  const normalizedWhole = BigInt(whole).toString();
  const normalizedFraction = fraction.padEnd(2, "0");
  const cents = BigInt(`${normalizedWhole}${normalizedFraction}`);
  if (cents <= 0n || cents > maximumAmountCents) return null;
  return `${normalizedWhole}.${normalizedFraction}`;
}

function formatAmount(amount: string) {
  const [whole, fraction = ""] = amount.split(".");
  const grouped = whole.replace(/\B(?=(\d{3})+(?!\d))/g, ".");
  return `R$ ${grouped},${fraction.padEnd(2, "0")}`;
}

function formatDueDate(dueDate: string) {
  const [year, month, day] = dueDate.split("-");
  return `${day}/${month}/${year}`;
}

export function PaguaiPage({
  draftClient = client,
}: {
  draftClient?: PaguaiDraftClient;
}) {
  const { bootstrap } = useSession();
  const companies = useMemo(
    () =>
      (bootstrap?.companies ?? []).filter((company) =>
        capabilityNames.some((name) => hasCompanyCapability(company, name)),
      ),
    [bootstrap],
  );
  const [companyId, setCompanyId] = useState("");
  const [drafts, setDrafts] = useState<readonly PaguaiDraft[]>([]);
  const [selected, setSelected] = useState<PaguaiDraft | null>(null);
  const [status, setStatus] = useState<"idle" | "loading" | "submitting">(
    "idle",
  );
  const [error, setError] = useState<PaguaiDraftError | null>(null);
  const [requestId, setRequestId] = useState(() => crypto.randomUUID());
  const titleId = useId();
  const descriptionId = useId();
  const amountId = useId();
  const dueDateId = useId();
  const beneficiaryId = useId();
  const proofRequirementId = useId();
  const resultRef = useRef<HTMLDivElement>(null);
  const companyIdRef = useRef(companyId);

  useEffect(() => {
    companyIdRef.current = companyId;
  }, [companyId]);

  const selectCompany = useCallback(
    (nextCompanyId: string) => {
      if (nextCompanyId === companyId) return;
      companyIdRef.current = nextCompanyId;
      setCompanyId(nextCompanyId);
      setDrafts([]);
      setSelected(null);
      setError(null);
      setStatus(nextCompanyId ? "loading" : "idle");
    },
    [companyId],
  );

  useEffect(() => {
    if (!companyId && companies[0]) selectCompany(companies[0].companyId);
  }, [companies, companyId, selectCompany]);
  useEffect(() => {
    if (
      !companyId ||
      !hasCompanyCapability(
        companies.find((company) => company.companyId === companyId) ?? {
          capabilities: [],
        },
        "paguai.draft.read",
      )
    ) {
      setDrafts([]);
      setSelected(null);
      setError(null);
      setStatus("idle");
      return;
    }
    let current = true;
    const requestedCompanyId = companyId;
    setDrafts([]);
    setStatus("loading");
    setError(null);
    setSelected(null);
    void draftClient.list(companyId).then((result) => {
      if (!current || companyIdRef.current !== requestedCompanyId) return;
      setStatus("idle");
      if (result.ok) setDrafts(result.value);
      else {
        setDrafts([]);
        setError(result.error);
      }
    });
    return () => {
      current = false;
    };
  }, [companyId, companies, draftClient]);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!companyId || status === "submitting") return;
    const form = event.currentTarget;
    const submittedCompanyId = companyId;
    const data = new FormData(form);
    const amount = amountFromReais(String(data.get("amountReais") ?? ""));
    const proofRequirement = String(data.get("proofRequirement") ?? "");
    if (
      amount === null ||
      (proofRequirement !== "optional" && proofRequirement !== "required")
    ) {
      setError(400);
      resultRef.current?.focus();
      return;
    }
    setStatus("submitting");
    setError(null);
    const result = await draftClient.create({
      companyId: submittedCompanyId,
      requestId,
      title: String(data.get("title") ?? ""),
      description: String(data.get("description") ?? ""),
      amount,
      currency: "BRL",
      dueDate: String(data.get("dueDate") ?? ""),
      beneficiaryDisplayName: String(data.get("beneficiaryDisplayName") ?? ""),
      proofRequirement: proofRequirement as "optional" | "required",
    });
    if (companyIdRef.current !== submittedCompanyId) return;
    setStatus("idle");
    if (!result.ok) {
      setError(result.error);
      resultRef.current?.focus();
      return;
    }
    setDrafts((current) => [
      result.value,
      ...current.filter((draft) => draft.id !== result.value.id),
    ]);
    setSelected(result.value);
    form.reset();
    setRequestId(crypto.randomUUID());
    resultRef.current?.focus();
  }

  const canCreate =
    !!companyId &&
    hasCompanyCapability(
      companies.find((company) => company.companyId === companyId) ?? {
        capabilities: [],
      },
      "paguai.draft.create",
    );
  return (
    <Stack spacing={3}>
      <PageHeader
        title="Paguai"
        description="Rascunhos de solicitações financeiras. O MVP não cria cobrança ou pagamento."
      />
      {!companies.length ? (
        <Alert severity="info">Nenhuma empresa tem acesso ao Paguai.</Alert>
      ) : (
        <>
          <TextField
            select
            label="Empresa"
            disabled={status === "submitting"}
            onChange={(event) => selectCompany(event.target.value)}
            value={companyId}
          >
            {companies.map((company) => (
              <MenuItem key={company.membershipId} value={company.companyId}>
                {company.companyName}
              </MenuItem>
            ))}
          </TextField>
          {canCreate ? (
            <Card component="section" variant="outlined">
              <CardContent>
                <Box component="form" onSubmit={submit}>
                  <Stack spacing={2}>
                    <Typography component="h2" variant="h6">
                      Novo rascunho
                    </Typography>
                    <TextField
                      id={titleId}
                      slotProps={{ htmlInput: { maxLength: 200 } }}
                      label="Título"
                      name="title"
                      required
                    />
                    <TextField
                      id={amountId}
                      inputMode="decimal"
                      label="Valor (R$)"
                      name="amountReais"
                      placeholder="1.250,00"
                      required
                    />
                    <TextField
                      id={dueDateId}
                      label="Vencimento"
                      name="dueDate"
                      slotProps={{
                        htmlInput: { min: "0001-01-01", type: "date" },
                      }}
                      required
                    />
                    <TextField
                      id={beneficiaryId}
                      label="Beneficiário (informativo)"
                      name="beneficiaryDisplayName"
                      slotProps={{ htmlInput: { maxLength: 200 } }}
                      required
                    />
                    <TextField
                      id={proofRequirementId}
                      label="Comprovante"
                      name="proofRequirement"
                      select
                      defaultValue=""
                      required
                    >
                      <MenuItem value="" disabled>
                        Selecione
                      </MenuItem>
                      <MenuItem value="required">Obrigatório</MenuItem>
                      <MenuItem value="optional">Opcional</MenuItem>
                    </TextField>
                    <TextField
                      id={descriptionId}
                      slotProps={{ htmlInput: { maxLength: 4000 } }}
                      label="Descrição"
                      minRows={3}
                      multiline
                      name="description"
                      required
                    />
                    <Button
                      disabled={status === "submitting"}
                      type="submit"
                      variant="contained"
                    >
                      {status === "submitting" ? "Criando…" : "Criar rascunho"}
                    </Button>
                  </Stack>
                </Box>
              </CardContent>
            </Card>
          ) : (
            <Alert severity="info">
              Você pode consultar rascunhos nesta empresa, mas não criar novos.
            </Alert>
          )}
          <Box aria-live="polite" ref={resultRef} tabIndex={-1}>
            {error ? <Alert severity="error">{message(error)}</Alert> : null}
          </Box>
          {selected ? (
            <Card component="section" variant="outlined">
              <CardContent>
                <Stack spacing={1}>
                  <Typography component="h2" variant="h6">
                    {selected.title}
                  </Typography>
                  <Typography>{selected.description}</Typography>
                  <Typography color="text.secondary">
                    Estado: {selected.state} · versão {selected.version}
                  </Typography>
                  <Typography>{formatAmount(selected.amount)}</Typography>
                  <Typography>
                    Vencimento: {formatDueDate(selected.dueDate)}
                  </Typography>
                  <Typography>
                    Beneficiário: {selected.beneficiaryDisplayName}{" "}
                    (informativo)
                  </Typography>
                  <Typography>
                    Comprovante:{" "}
                    {selected.proofRequirement === "required"
                      ? "obrigatório"
                      : "opcional"}
                  </Typography>
                  <Typography color="text.secondary">
                    Criado em {new Date(selected.createdAt).toLocaleString()}
                  </Typography>
                </Stack>
              </CardContent>
            </Card>
          ) : null}
          <Stack component="section" spacing={1}>
            <Typography component="h2" variant="h6">
              Solicitações
            </Typography>
            {status === "loading" ? (
              <Typography>Carregando…</Typography>
            ) : !drafts.length ? (
              <Alert severity="info">Nenhum rascunho nesta empresa.</Alert>
            ) : (
              drafts.map((draft) => (
                <Card key={draft.id} variant="outlined">
                  <CardActionArea
                    onClick={() => {
                      const requestedCompanyId = companyId;
                      void draftClient
                        .detail(requestedCompanyId, draft.id)
                        .then((result) => {
                          if (companyIdRef.current !== requestedCompanyId)
                            return;
                          if (result.ok) setSelected(result.value);
                          else setError(result.error);
                        });
                    }}
                  >
                    <CardContent>
                      <Typography component="h3" variant="subtitle1">
                        {draft.title}
                      </Typography>
                      <Typography color="text.secondary">
                        {draft.state} · versão {draft.version}
                      </Typography>
                      <Typography>{formatAmount(draft.amount)}</Typography>
                      <Typography color="text.secondary">
                        Vencimento: {formatDueDate(draft.dueDate)}
                      </Typography>
                    </CardContent>
                  </CardActionArea>
                </Card>
              ))
            )}
          </Stack>
        </>
      )}
    </Stack>
  );
}
