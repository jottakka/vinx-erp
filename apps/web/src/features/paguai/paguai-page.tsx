import AddOutlinedIcon from "@mui/icons-material/AddOutlined";
import AttachFileOutlinedIcon from "@mui/icons-material/AttachFileOutlined";
import VisibilityOutlinedIcon from "@mui/icons-material/VisibilityOutlined";
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Stack,
  Typography,
} from "@mui/material";
import { useEffect, useMemo, useRef, useState } from "react";
import { AuthorizedCompanyFilter } from "../../shared/ui/authorized-company-filter";
import { CompanyProvider } from "../../shared/ui/company-context";
import { CompanyIdentity } from "../../shared/ui/company-identity";
import { EntityDialog } from "../../shared/ui/entity-dialog";
import { PageHeader } from "../../shared/ui/page-header";
import { StatusChip } from "../../shared/ui/status-chip";
import { resolveCompanyEntityPermissions } from "../auth/company-capabilities";
import { useSession } from "../auth/session-provider";
import { PaguaiCreateDialog } from "./paguai-create-dialog";
import {
  createPaguaiDraftClient,
  type PaguaiDraft,
  type PaguaiDraftError,
} from "./paguai-draft-client";
import { formatAmount, formatDueDate } from "./paguai-format";
import { createPaguaiInvoiceUploader } from "./paguai-proof-uploader";

const client = createPaguaiDraftClient();
const readCapability = "paguai.draft.read";
const createCapability = "paguai.draft.create";
type PaguaiDraftClient = ReturnType<typeof createPaguaiDraftClient>;

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

const statePresentation: Record<
  string,
  { label: string; tone: "default" | "info" | "success" | "warning" | "error" }
> = {
  rascunho: { label: "Rascunho", tone: "default" },
  em_analise: { label: "Em análise", tone: "info" },
  aprovado: { label: "Aprovado", tone: "success" },
  rejeitado: { label: "Rejeitado", tone: "error" },
  cancelado: { label: "Cancelado", tone: "default" },
  pago: { label: "Pago", tone: "success" },
};

function presentationFor(state: string) {
  return statePresentation[state] ?? { label: state, tone: "default" as const };
}

function sortDrafts(left: PaguaiDraft, right: PaguaiDraft) {
  return (
    right.createdAt.localeCompare(left.createdAt) ||
    left.companyId.localeCompare(right.companyId) ||
    left.id.localeCompare(right.id)
  );
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
        company.capabilities.some(
          (capability) =>
            capability.name === readCapability &&
            capability.scopes.includes("Company"),
        ),
      ),
    [bootstrap],
  );
  const [selectedCompanyIds, setSelectedCompanyIds] = useState<string[]>([]);
  const [drafts, setDrafts] = useState<readonly PaguaiDraft[]>([]);
  const [selected, setSelected] = useState<PaguaiDraft | null>(null);
  const [viewOpen, setViewOpen] = useState(false);
  const [createOpen, setCreateOpen] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  const [status, setStatus] = useState<"idle" | "loading" | "submitting">(
    "idle",
  );
  const [error, setError] = useState<PaguaiDraftError | null>(null);
  const [partialFailures, setPartialFailures] = useState<
    readonly { companyId: string; error: PaguaiDraftError }[]
  >([]);
  const [requestId, setRequestId] = useState(() => crypto.randomUUID());
  const [createDialogInstance, setCreateDialogInstance] = useState(0);
  const companyIdRef = useRef("");
  const detailRequestRef = useRef(0);

  const requestedCompanies = useMemo(() => {
    if (!selectedCompanyIds.length) return companies;
    const selectedIds = new Set(selectedCompanyIds);
    return companies.filter((company) => selectedIds.has(company.companyId));
  }, [companies, selectedCompanyIds]);
  const companyById = useMemo(
    () => new Map(companies.map((company) => [company.companyId, company])),
    [companies],
  );
  const createCompany = useMemo(() => {
    if (selectedCompanyIds.length === 1)
      return companies.find(
        (company) => company.companyId === selectedCompanyIds[0],
      );
    return companies.length === 1 ? companies[0] : undefined;
  }, [companies, selectedCompanyIds]);
  const permissions = useMemo(
    () =>
      resolveCompanyEntityPermissions(createCompany, {
        create: createCapability,
      }),
    [createCompany],
  );
  const invoiceUploader = useMemo(() => createPaguaiInvoiceUploader(), []);

  useEffect(() => {
    const validIds = new Set(companies.map((company) => company.companyId));
    setSelectedCompanyIds((current) =>
      current.filter((companyId) => validIds.has(companyId)),
    );
  }, [companies]);

  useEffect(() => {
    if (!requestedCompanies.length) {
      setDrafts([]);
      setPartialFailures([]);
      setError(null);
      setStatus("idle");
      return;
    }
    let current = true;
    setDrafts([]);
    setSelected(null);
    setViewOpen(false);
    setError(null);
    setPartialFailures([]);
    setStatus("loading");
    void Promise.all(
      requestedCompanies.map(async (company) => ({
        companyId: company.companyId,
        result: await draftClient.list(company.companyId),
      })),
    ).then((results) => {
      if (!current) return;
      const successes: PaguaiDraft[] = [];
      const failures: { companyId: string; error: PaguaiDraftError }[] = [];
      for (const { companyId, result } of results) {
        if (result.ok)
          successes.push(
            ...result.value.filter((draft) => draft.companyId === companyId),
          );
        else failures.push({ companyId, error: result.error });
      }
      setDrafts(successes.sort(sortDrafts));
      setPartialFailures(failures);
      setError(
        failures.length === requestedCompanies.length
          ? (failures[0]?.error ?? null)
          : null,
      );
      setStatus("idle");
    });
    return () => {
      current = false;
    };
  }, [draftClient, requestedCompanies]);

  const uploaderAdapter = useMemo(() => {
    if (!createCompany) return undefined;
    return (file: File, signal: AbortSignal) =>
      invoiceUploader
        .upload({ companyId: createCompany.companyId, file, signal })
        .then((result) =>
          result.ok
            ? {
                ok: true as const,
                fileId: result.invoice.fileId,
                fileName: result.invoice.fileName,
                mimeType: result.invoice.mimeType,
                sizeBytes: result.invoice.sizeBytes,
              }
            : {
                ok: false as const,
                error: {
                  kind: result.error.kind,
                  message: "Falha ao anexar a fatura.",
                },
              },
        );
  }, [createCompany, invoiceUploader]);

  async function submit(values: {
    amount: string;
    beneficiaryDisplayName: string;
    description: string;
    dueDate: string;
    invoiceFileId: string | null;
    proofRequirement: "optional" | "required";
    title: string;
  }) {
    if (!createCompany || status === "submitting") return;
    const companyId = createCompany.companyId;
    companyIdRef.current = companyId;
    setStatus("submitting");
    setError(null);
    const result = await draftClient.create({
      companyId,
      requestId,
      title: values.title,
      description: values.description,
      amount: values.amount,
      currency: "BRL",
      dueDate: values.dueDate,
      beneficiaryDisplayName: values.beneficiaryDisplayName,
      invoiceFileId: values.invoiceFileId,
      proofRequirement: values.proofRequirement,
    });
    if (companyIdRef.current !== companyId) return;
    setStatus("idle");
    if (!result.ok) {
      setError(result.error);
      setCreateError(message(result.error));
      return;
    }
    setDrafts((current) =>
      [
        ...current.filter((draft) => draft.id !== result.value.id),
        result.value,
      ].sort(sortDrafts),
    );
    setSelected(result.value);
    setCreateError(null);
    setCreateDialogInstance((current) => current + 1);
    setCreateOpen(false);
    setRequestId(crypto.randomUUID());
  }

  function openView(draft: PaguaiDraft) {
    const request = detailRequestRef.current + 1;
    detailRequestRef.current = request;
    void draftClient.detail(draft.companyId, draft.id).then((result) => {
      if (detailRequestRef.current !== request) return;
      if (result.ok) {
        setSelected(result.value);
        setViewOpen(true);
      } else setError(result.error);
    });
  }

  return (
    <CompanyProvider company={createCompany ?? null}>
      <Stack spacing={3}>
        <PageHeader
          actions={
            permissions.canCreate ? (
              <Button
                onClick={() => setCreateOpen(true)}
                startIcon={<AddOutlinedIcon />}
                variant="contained"
              >
                Nova solicitação
              </Button>
            ) : null
          }
          description="Solicitações financeiras por empresa. O MVP não cria cobrança ou pagamento."
          title="Paguai"
        />
        {!companies.length ? (
          <Alert severity="info">Nenhuma empresa tem acesso ao Paguai.</Alert>
        ) : (
          <>
            <AuthorizedCompanyFilter
              companies={companies}
              disabled={status === "submitting"}
              label="Empresa"
              onChange={setSelectedCompanyIds}
              selectedCompanyIds={selectedCompanyIds}
            />
            {selectedCompanyIds.length === 0 && companies.length > 1 ? (
              <Alert severity="info">
                Selecione uma única empresa para criar uma solicitação.
              </Alert>
            ) : null}
            {error ? <Alert severity="error">{message(error)}</Alert> : null}
            {partialFailures.map(({ companyId, error: failure }) => (
              <Alert key={companyId} severity="warning">
                {companyById.get(companyId)?.companyName ?? "Empresa"}{" "}
                indisponível: {message(failure)}
              </Alert>
            ))}
            <Stack component="section" spacing={1.5}>
              <Typography component="h2" variant="h6">
                Solicitações
              </Typography>
              {status === "loading" ? (
                <Typography aria-live="polite" role="status">
                  Carregando…
                </Typography>
              ) : !drafts.length ? (
                <Alert severity="info">Nenhuma solicitação encontrada.</Alert>
              ) : (
                drafts.map((draft) => {
                  const company = companyById.get(draft.companyId);
                  const state = presentationFor(draft.state);
                  return (
                    <Card
                      key={`${draft.companyId}-${draft.id}`}
                      variant="outlined"
                    >
                      <CardContent>
                        <Stack spacing={1}>
                          <Stack
                            direction={{ sm: "row", xs: "column" }}
                            spacing={1}
                            sx={{ alignItems: { sm: "center" } }}
                          >
                            <Box sx={{ flex: 1, minWidth: 0 }}>
                              <Typography component="h3" variant="subtitle1">
                                {draft.title}
                              </Typography>
                              {company ? (
                                <CompanyIdentity
                                  companyId={company.companyId}
                                  companyName={company.companyName}
                                />
                              ) : null}
                            </Box>
                            <StatusChip label={state.label} tone={state.tone} />
                            <Button
                              onClick={() => openView(draft)}
                              startIcon={<VisibilityOutlinedIcon />}
                              variant="outlined"
                            >
                              Visualizar
                            </Button>
                          </Stack>
                          <Stack
                            direction="row"
                            spacing={2}
                            sx={{ alignItems: "center" }}
                          >
                            <Typography>
                              {formatAmount(draft.amount)}
                            </Typography>
                            <Typography color="text.secondary">
                              Vencimento: {formatDueDate(draft.dueDate)}
                            </Typography>
                          </Stack>
                        </Stack>
                      </CardContent>
                    </Card>
                  );
                })
              )}
            </Stack>
          </>
        )}
      </Stack>
      <PaguaiCreateDialog
        errorMessage={createError}
        onClose={() => {
          setCreateError(null);
          setCreateOpen(false);
          setCreateDialogInstance((current) => current + 1);
        }}
        key={createDialogInstance}
        onSubmit={(values) => void submit(values)}
        open={createOpen}
        submitting={status === "submitting"}
        uploader={uploaderAdapter}
      />
      <DraftViewDialog
        draft={selected}
        onClose={() => setViewOpen(false)}
        open={viewOpen}
        companyName={
          selected
            ? (companyById.get(selected.companyId)?.companyName ?? "Empresa")
            : "Empresa"
        }
      />
    </CompanyProvider>
  );
}

function DraftViewDialog({
  companyName,
  draft,
  onClose,
  open,
}: {
  companyName: string;
  draft: PaguaiDraft | null;
  onClose: () => void;
  open: boolean;
}) {
  return (
    <EntityDialog
      description={`Solicitação de ${companyName}. Apenas leitura.`}
      onClose={onClose}
      open={open}
      title="Visualizar solicitação"
    >
      {draft ? (
        <Stack spacing={1.5}>
          <CompanyIdentity
            companyId={draft.companyId}
            companyName={companyName}
          />
          <Typography component="p" variant="subtitle1">
            {draft.title}
          </Typography>
          <Typography>{draft.description}</Typography>
          <Typography>
            Estado: {presentationFor(draft.state).label} · versão{" "}
            {draft.version}
          </Typography>
          <Typography>{formatAmount(draft.amount)}</Typography>
          <Typography>Vencimento: {formatDueDate(draft.dueDate)}</Typography>
          <Typography>
            Beneficiário: {draft.beneficiaryDisplayName} (informativo)
          </Typography>
          <Typography>
            Comprovante de pagamento:{" "}
            {draft.proofRequirement === "required" ? "obrigatório" : "opcional"}
          </Typography>
          <Stack direction="row" spacing={1} sx={{ alignItems: "center" }}>
            <AttachFileOutlinedIcon color="action" fontSize="small" />
            <Typography>
              {draft.invoiceFileId
                ? `Fatura anexada: ${draft.invoiceFileId}`
                : "Nenhuma fatura anexada."}
            </Typography>
          </Stack>
          <Typography color="text.secondary" variant="caption">
            Criado em {new Date(draft.createdAt).toLocaleString()}
          </Typography>
        </Stack>
      ) : null}
    </EntityDialog>
  );
}
