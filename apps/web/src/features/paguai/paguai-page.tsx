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
      503: "Paguai está indisponível. Tente novamente com o mesmo identificador.",
      network:
        "Não foi possível alcançar o servidor. Tente novamente com o mesmo identificador.",
      protocol: "A resposta do servidor não pôde ser validada.",
    } as const
  )[error];
}

export function PaguaiPage() {
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
  const resultRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!companyId && companies[0]) setCompanyId(companies[0].companyId);
  }, [companies, companyId]);
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
      return;
    }
    let current = true;
    setStatus("loading");
    setError(null);
    setSelected(null);
    void client.list(companyId).then((result) => {
      if (!current) return;
      setStatus("idle");
      if (result.ok) setDrafts(result.value);
      else setError(result.error);
    });
    return () => {
      current = false;
    };
  }, [companyId, companies]);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!companyId || status === "submitting") return;
    const data = new FormData(event.currentTarget);
    setStatus("submitting");
    setError(null);
    const result = await client.create({
      companyId,
      requestId,
      title: String(data.get("title") ?? ""),
      description: String(data.get("description") ?? ""),
    });
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
    event.currentTarget.reset();
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
        description="Rascunhos de solicitações. Este MVP registra somente metadados; não cria cobrança ou pagamento."
      />
      {!companies.length ? (
        <Alert severity="info">Nenhuma empresa tem acesso ao Paguai.</Alert>
      ) : (
        <>
          <TextField
            select
            label="Empresa"
            onChange={(event) => setCompanyId(event.target.value)}
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
            {error ? (
              <Alert severity="error">{message(error)}</Alert>
            ) : selected ? (
              <Alert severity="success">
                Rascunho selecionado: {selected.title}
              </Alert>
            ) : null}
          </Box>
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
                    onClick={() =>
                      void client.detail(companyId, draft.id).then((result) => {
                        if (result.ok) setSelected(result.value);
                        else setError(result.error);
                      })
                    }
                  >
                    <CardContent>
                      <Typography component="h3" variant="subtitle1">
                        {draft.title}
                      </Typography>
                      <Typography color="text.secondary">
                        {draft.state} · versão {draft.version}
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
