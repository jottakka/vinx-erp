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
import { useLocation, useNavigate } from "react-router-dom";
import {
  paguaiPath,
  readPaguaiProposalId,
} from "../../shared/routing/app-paths";
import { AuthorizedCompanyFilter } from "../../shared/ui/authorized-company-filter";
import { CompanyProvider } from "../../shared/ui/company-context";
import { CompanyIdentity } from "../../shared/ui/company-identity";
import { EntityDialog } from "../../shared/ui/entity-dialog";
import { useFeedback } from "../../shared/ui/feedback-provider";
import { PageHeader } from "../../shared/ui/page-header";
import { StatusChip } from "../../shared/ui/status-chip";
import { resolveCompanyEntityPermissions } from "../auth/company-capabilities";
import { useSession } from "../auth/session-provider";
import { PaguaiCreateDialog } from "./paguai-create-dialog";
import {
  type CreatePaguaiDraftInput,
  createPaguaiDraftClient,
  type PaguaiDraft,
  type PaguaiDraftError,
  type PaguaiProposal,
} from "./paguai-draft-client";
import { formatAmount, formatDueDate } from "./paguai-format";
import { createPaguaiInvoiceUploader } from "./paguai-proof-uploader";
import { PaguaiProposalReviewDialog } from "./paguai-proposal-review-dialog";

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
      409: "Esta solicitação entrou em conflito com dados já enviados. Revise os dados e crie uma nova solicitação.",
      429: "Muitas tentativas. Aguarde antes de tentar novamente.",
      503: "Paguai está indisponível. Tente novamente com os mesmos dados.",
      network:
        "Não foi possível alcançar o servidor. Tente novamente com os mesmos dados.",
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

type PaguaiCreatePayload = Omit<CreatePaguaiDraftInput, "requestId">;
type PaguaiCreateAttempt = {
  payload: PaguaiCreatePayload;
  requestId: string;
};
type PaguaiProposalOperation = "confirm" | "reject";
type PaguaiProposalMutationAttempt = {
  companyId: string;
  operation: PaguaiProposalOperation;
  proposalId: string;
  requestId: string;
};

function isAmbiguousProposalError(error: PaguaiDraftError) {
  return error === "network" || error === "protocol" || error === 503;
}

function sameCreatePayload(
  left: PaguaiCreatePayload,
  right: PaguaiCreatePayload,
) {
  return (
    left.amount === right.amount &&
    left.beneficiaryDisplayName === right.beneficiaryDisplayName &&
    left.companyId === right.companyId &&
    left.currency === right.currency &&
    left.description === right.description &&
    left.dueDate === right.dueDate &&
    left.invoiceFileId === right.invoiceFileId &&
    left.proofRequirement === right.proofRequirement &&
    left.title === right.title
  );
}

function scopeKeyFor(
  bootstrap: ReturnType<typeof useSession>["bootstrap"],
  selectedCompanyIds: readonly string[],
  createCompanyId: string | undefined,
) {
  return JSON.stringify({
    companies: (bootstrap?.companies ?? [])
      .map((company) => ({
        capabilities: company.capabilities
          .map(
            (capability) =>
              `${capability.name}:${[...capability.scopes].sort().join(",")}`,
          )
          .sort(),
        companyId: company.companyId,
        membershipId: company.membershipId,
      }))
      .sort(
        (left, right) =>
          left.companyId.localeCompare(right.companyId) ||
          left.membershipId.localeCompare(right.membershipId),
      ),
    createCompanyId: createCompanyId ?? null,
    selectedCompanyIds: [...new Set(selectedCompanyIds)].sort(),
    userId: bootstrap?.user.id ?? null,
  });
}

export function PaguaiPage({
  draftClient = client,
}: {
  draftClient?: PaguaiDraftClient;
}) {
  const { bootstrap } = useSession();
  const { notify } = useFeedback();
  const location = useLocation();
  const navigate = useNavigate();
  const proposalId = readPaguaiProposalId(location.search);
  const hasInvalidProposalId =
    new URLSearchParams(location.search).has("proposal") && proposalId === null;
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
  const [createRecoveryRequired, setCreateRecoveryRequired] = useState(false);
  const [status, setStatus] = useState<"idle" | "loading" | "submitting">(
    "idle",
  );
  const [error, setError] = useState<PaguaiDraftError | null>(null);
  const [partialFailures, setPartialFailures] = useState<
    readonly { companyId: string; error: PaguaiDraftError }[]
  >([]);
  const [createDialogInstance, setCreateDialogInstance] = useState(0);
  const [proposal, setProposal] = useState<PaguaiProposal | null>(null);
  const [proposalError, setProposalError] = useState<PaguaiDraftError | null>(
    null,
  );
  const [proposalLoading, setProposalLoading] = useState(false);
  const [proposalSubmitting, setProposalSubmitting] = useState(false);

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
  const scopeKey = useMemo(
    () => scopeKeyFor(bootstrap, selectedCompanyIds, createCompany?.companyId),
    [bootstrap, createCompany?.companyId, selectedCompanyIds],
  );
  const scopeGenerationRef = useRef(0);
  const scopeKeyRef = useRef(scopeKey);
  const createAttemptRef = useRef<PaguaiCreateAttempt | null>(null);
  const proposalMutationAttemptRef =
    useRef<PaguaiProposalMutationAttempt | null>(null);
  const detailRequestRef = useRef(0);
  const proposalRequestRef = useRef(0);

  useEffect(() => {
    const validIds = new Set(companies.map((company) => company.companyId));
    setSelectedCompanyIds((current) =>
      current.filter((companyId) => validIds.has(companyId)),
    );
  }, [companies]);

  useEffect(() => {
    if (scopeKeyRef.current === scopeKey) return;
    scopeKeyRef.current = scopeKey;
    scopeGenerationRef.current += 1;
    createAttemptRef.current = null;
    proposalMutationAttemptRef.current = null;
    detailRequestRef.current += 1;
    setCreateError(null);
    setCreateRecoveryRequired(false);
    setCreateOpen(false);
    setCreateDialogInstance((current) => current + 1);
    setError(null);
    setSelected(null);
    setStatus((current) => (current === "submitting" ? "idle" : current));
    setViewOpen(false);
    setProposal(null);
    setProposalError(null);
    setProposalLoading(false);
    setProposalSubmitting(false);
  }, [scopeKey]);

  useEffect(() => {
    proposalMutationAttemptRef.current = null;
    const request = proposalRequestRef.current + 1;
    proposalRequestRef.current = request;
    setProposal(null);
    setProposalError(hasInvalidProposalId ? 400 : null);
    setProposalLoading(false);
    if (!proposalId || hasInvalidProposalId || !createCompany) return;

    setProposalLoading(true);
    void draftClient
      .getProposal(createCompany.companyId, proposalId)
      .then((result) => {
        if (proposalRequestRef.current !== request) return;
        setProposalLoading(false);
        if (!result.ok) {
          setProposalError(result.error);
          return;
        }
        if (result.value.companyId !== createCompany.companyId) {
          setProposalError(404);
          return;
        }
        setProposal(result.value);
      });
  }, [createCompany, draftClient, hasInvalidProposalId, proposalId]);

  function closeProposalReview() {
    proposalMutationAttemptRef.current = null;
    proposalRequestRef.current += 1;
    setProposal(null);
    setProposalError(null);
    navigate(paguaiPath, { replace: true });
  }

  async function resolveProposal(operation: PaguaiProposalOperation) {
    if (!proposal || !createCompany || proposalSubmitting) return;
    const scopeGeneration = scopeGenerationRef.current;
    setProposalSubmitting(true);
    setProposalError(null);
    const previousAttempt = proposalMutationAttemptRef.current;
    const attempt =
      previousAttempt?.companyId === createCompany.companyId &&
      previousAttempt.proposalId === proposal.id &&
      previousAttempt.operation === operation
        ? previousAttempt
        : {
            companyId: createCompany.companyId,
            operation,
            proposalId: proposal.id,
            requestId: crypto.randomUUID(),
          };
    proposalMutationAttemptRef.current = attempt;
    if (operation === "confirm") {
      const result = await draftClient.confirmProposal(
        createCompany.companyId,
        proposal.id,
        attempt.requestId,
      );
      // Resposta tardia após troca de escopo: descartada antes de qualquer
      // commit local (mesmo padrão de createAttempt/openView).
      if (scopeGeneration !== scopeGenerationRef.current) return;
      setProposalSubmitting(false);
      if (!result.ok) {
        if (!isAmbiguousProposalError(result.error)) {
          proposalMutationAttemptRef.current = null;
        }
        setProposalError(result.error);
        return;
      }
      proposalMutationAttemptRef.current = null;
      setDrafts((current) =>
        [
          ...current.filter((draft) => draft.id !== result.value.id),
          result.value,
        ].sort(sortDrafts),
      );
      notify("Solicitação criada a partir da proposta.");
      closeProposalReview();
      return;
    }

    const result = await draftClient.rejectProposal(
      createCompany.companyId,
      proposal.id,
      attempt.requestId,
    );
    if (scopeGeneration !== scopeGenerationRef.current) return;
    setProposalSubmitting(false);
    if (!result.ok) {
      if (!isAmbiguousProposalError(result.error)) {
        proposalMutationAttemptRef.current = null;
      }
      setProposalError(result.error);
      return;
    }
    proposalMutationAttemptRef.current = null;
    notify("Proposta rejeitada.", "info");
    closeProposalReview();
  }

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

  async function createAttempt(attempt: PaguaiCreateAttempt) {
    const scopeGeneration = scopeGenerationRef.current;
    setStatus("submitting");
    setError(null);
    const result = await draftClient.create({
      ...attempt.payload,
      requestId: attempt.requestId,
    });
    if (
      scopeGeneration !== scopeGenerationRef.current ||
      createAttemptRef.current !== attempt
    )
      return;
    setStatus("idle");
    if (!result.ok) {
      setError(result.error);
      if (result.error === "network") {
        setCreateError(null);
        setCreateRecoveryRequired(true);
        return;
      }
      setCreateRecoveryRequired(false);
      setCreateError(message(result.error));
      if (result.error === 409) createAttemptRef.current = null;
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
    setCreateRecoveryRequired(false);
    createAttemptRef.current = null;
    setCreateDialogInstance((current) => current + 1);
    setCreateOpen(false);
  }

  async function submit(values: {
    amount: string;
    beneficiaryDisplayName: string;
    description: string;
    dueDate: string;
    invoiceFileId: string | null;
    proofRequirement: "optional" | "required";
    title: string;
  }) {
    if (!createCompany || status === "submitting" || createRecoveryRequired)
      return;
    const payload: PaguaiCreatePayload = {
      amount: values.amount,
      beneficiaryDisplayName: values.beneficiaryDisplayName,
      companyId: createCompany.companyId,
      currency: "BRL",
      description: values.description,
      dueDate: values.dueDate,
      invoiceFileId: values.invoiceFileId,
      proofRequirement: values.proofRequirement,
      title: values.title,
    };
    const currentAttempt = createAttemptRef.current;
    const attempt =
      currentAttempt && sameCreatePayload(currentAttempt.payload, payload)
        ? currentAttempt
        : { payload, requestId: crypto.randomUUID() };
    createAttemptRef.current = attempt;
    await createAttempt(attempt);
  }

  function retryCreateAttempt() {
    const attempt = createAttemptRef.current;
    if (!attempt || !createRecoveryRequired || status === "submitting") return;
    void createAttempt(attempt);
  }

  function openView(draft: PaguaiDraft) {
    const request = detailRequestRef.current + 1;
    const scopeGeneration = scopeGenerationRef.current;
    detailRequestRef.current = request;
    void draftClient.detail(draft.companyId, draft.id).then((result) => {
      if (
        detailRequestRef.current !== request ||
        scopeGeneration !== scopeGenerationRef.current
      )
        return;
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
                disabled={status === "submitting" || createRecoveryRequired}
                onClick={() => {
                  if (createRecoveryRequired) return;
                  setCreateError(null);
                  setCreateOpen(true);
                }}
                startIcon={<AddOutlinedIcon />}
                variant="contained"
              >
                Nova solicitação
              </Button>
            ) : null
          }
          description="Acompanhe solicitações financeiras das empresas autorizadas."
          title="Paguai"
        />
        {!companies.length ? (
          <Alert severity="info">Nenhuma empresa tem acesso ao Paguai.</Alert>
        ) : (
          <>
            <AuthorizedCompanyFilter
              companies={companies}
              disabled={status === "submitting" || createRecoveryRequired}
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
            {proposalError ? (
              <Alert severity="error">
                {proposalError === 400
                  ? "A proposta indicada é inválida."
                  : proposalError === 409
                    ? "Esta proposta expirou ou já foi resolvida."
                    : message(proposalError)}
              </Alert>
            ) : null}
            {proposalId && !createCompany && !hasInvalidProposalId ? (
              <Alert severity="info">
                Selecione uma única empresa para revisar a proposta.
              </Alert>
            ) : null}
            {proposalLoading ? (
              <Typography aria-live="polite" role="status">
                Carregando proposta…
              </Typography>
            ) : null}
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
                              Pagar até: {formatDueDate(draft.dueDate)}
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
        company={
          createCompany
            ? {
                companyId: createCompany.companyId,
                companyName: createCompany.companyName,
              }
            : null
        }
        errorMessage={createError}
        onClose={() => {
          if (createRecoveryRequired) return;
          createAttemptRef.current = null;
          setCreateError(null);
          setCreateRecoveryRequired(false);
          setError(null);
          setCreateOpen(false);
          setCreateDialogInstance((current) => current + 1);
        }}
        key={createDialogInstance}
        onRetry={retryCreateAttempt}
        onSubmit={(values) => void submit(values)}
        open={createOpen}
        recoveryRequired={createRecoveryRequired}
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
      <PaguaiProposalReviewDialog
        companyName={
          proposal
            ? (companyById.get(proposal.companyId)?.companyName ?? "Empresa")
            : "Empresa"
        }
        onClose={closeProposalReview}
        onConfirm={() => void resolveProposal("confirm")}
        onReject={() => void resolveProposal("reject")}
        open={proposal !== null}
        proposal={proposal}
        submitting={proposalSubmitting}
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
          <Typography>Estado: {presentationFor(draft.state).label}</Typography>
          <Typography>{formatAmount(draft.amount)}</Typography>
          <Typography>Pagar até: {formatDueDate(draft.dueDate)}</Typography>
          <Typography>Quem receberá: {draft.beneficiaryDisplayName}</Typography>
          <Typography>
            Comprovante de pagamento:{" "}
            {draft.proofRequirement === "required" ? "obrigatório" : "opcional"}
          </Typography>
          <Stack direction="row" spacing={1} sx={{ alignItems: "center" }}>
            <AttachFileOutlinedIcon color="action" fontSize="small" />
            <Typography>
              {draft.invoiceFileId
                ? "Fatura anexada"
                : "Nenhuma fatura anexada."}
            </Typography>
          </Stack>
          <Typography color="text.secondary" variant="caption">
            Criado em {new Date(draft.createdAt).toLocaleString("pt-BR")}
          </Typography>
        </Stack>
      ) : null}
    </EntityDialog>
  );
}
