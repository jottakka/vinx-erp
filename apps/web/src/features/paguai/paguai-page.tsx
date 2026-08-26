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
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { CompanyProvider } from "../../shared/ui/company-context";
import { CompanySelect } from "../../shared/ui/company-select";
import { EntityDialog } from "../../shared/ui/entity-dialog";
import { PageHeader } from "../../shared/ui/page-header";
import { resolveCompanyEntityPermissions } from "../auth/company-capabilities";
import { useSession } from "../auth/session-provider";
import { PaguaiCreateDialog } from "./paguai-create-dialog";
import {
  createPaguaiDraftClient,
  type PaguaiDraft,
  type PaguaiDraftError,
} from "./paguai-draft-client";
import { formatAmount, formatDueDate } from "./paguai-format";
import { createPaguaiProofUploader } from "./paguai-proof-uploader";

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
  const [companyId, setCompanyId] = useState("");
  const [drafts, setDrafts] = useState<readonly PaguaiDraft[]>([]);
  const [selected, setSelected] = useState<PaguaiDraft | null>(null);
  const [viewOpen, setViewOpen] = useState(false);
  const [createOpen, setCreateOpen] = useState(false);
  const [proofFileId, setProofFileId] = useState<string | null>(null);
  const [status, setStatus] = useState<"idle" | "loading" | "submitting">(
    "idle",
  );
  const [error, setError] = useState<PaguaiDraftError | null>(null);
  const [requestId, setRequestId] = useState(() => crypto.randomUUID());
  const companyIdRef = useRef(companyId);

  const proofUploader = useMemo(() => createPaguaiProofUploader(), []);

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
    if (!companyId) {
      setDrafts([]);
      setSelected(null);
      setError(null);
      setStatus("idle");
      return;
    }
    let current = true;
    setDrafts([]);
    setStatus("loading");
    setError(null);
    setSelected(null);
    void draftClient.list(companyId).then((result) => {
      if (!current || companyIdRef.current !== companyId) return;
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
  }, [companyId, draftClient]);

  const selectedCompany = companies.find(
    (company) => company.companyId === companyId,
  );
  const permissions = useMemo(
    () =>
      resolveCompanyEntityPermissions(selectedCompany, {
        create: createCapability,
      }),
    [selectedCompany],
  );

  const uploaderAdapter = useMemo(
    () => (file: File, signal: AbortSignal) =>
      proofUploader.upload({ companyId, file, signal }).then((result) =>
        result.ok
          ? {
              ok: true as const,
              fileId: result.proof.fileId,
              fileName: result.proof.fileName,
              mimeType: result.proof.mimeType,
              sizeBytes: result.proof.sizeBytes,
            }
          : {
              ok: false as const,
              error: {
                kind: result.error.kind,
                message: "Falha ao anexar.",
              },
            },
      ),
    [companyId, proofUploader],
  );

  async function submit(values: {
    amount: string;
    beneficiaryDisplayName: string;
    description: string;
    dueDate: string;
    proofFileId: string | null;
    proofRequirement: "optional" | "required";
    title: string;
  }) {
    if (!companyId || status === "submitting") return;
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
      proofRequirement: values.proofRequirement,
    });
    if (companyIdRef.current !== companyId) return;
    setStatus("idle");
    if (!result.ok) {
      setError(result.error);
      return;
    }
    setDrafts((current) => [
      result.value,
      ...current.filter((draft) => draft.id !== result.value.id),
    ]);
    setSelected(result.value);
    setProofFileId(values.proofFileId);
    setCreateOpen(false);
    setRequestId(crypto.randomUUID());
  }

  function openView(draft: PaguaiDraft) {
    void draftClient.detail(companyId, draft.id).then((result) => {
      if (companyIdRef.current !== companyId) return;
      if (result.ok) {
        setSelected(result.value);
        setProofFileId(null);
        setViewOpen(true);
      } else {
        setError(result.error);
      }
    });
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
                Nova solicitação
              </Button>
            ) : null
          }
          description="Rascunhos de solicitações financeiras por empresa. O MVP não cria cobrança ou pagamento."
          title="Paguai"
        />
        {!companies.length ? (
          <Alert severity="info">Nenhuma empresa tem acesso ao Paguai.</Alert>
        ) : (
          <>
            <CompanySelect
              companies={companies}
              disabled={status === "submitting"}
              label="Empresa"
              onChange={selectCompany}
              value={companyId}
            />
            {!permissions.canCreate ? (
              <Alert severity="info">
                Você pode consultar solicitações nesta empresa, mas não criar
                novas.
              </Alert>
            ) : null}
            <Box aria-live="polite">
              {error ? <Alert severity="error">{message(error)}</Alert> : null}
            </Box>
            <Stack component="section" spacing={1}>
              <Typography component="h2" variant="h6">
                Solicitações
              </Typography>
              {status === "loading" ? (
                <Typography>Carregando…</Typography>
              ) : !drafts.length ? (
                <Alert severity="info">
                  Nenhuma solicitação nesta empresa.
                </Alert>
              ) : (
                drafts.map((draft) => (
                  <Card key={draft.id} variant="outlined">
                    <CardContent>
                      <Stack
                        direction={{ xs: "column", sm: "row" }}
                        spacing={1}
                        sx={{ alignItems: { sm: "center" } }}
                      >
                        <Box sx={{ flex: 1, minWidth: 0 }}>
                          <Typography component="h3" variant="subtitle1">
                            {draft.title}
                          </Typography>
                          <Typography color="text.secondary">
                            {draft.state} · versão {draft.version}
                          </Typography>
                          <Typography>{formatAmount(draft.amount)}</Typography>
                        </Box>
                        <Button
                          onClick={() => openView(draft)}
                          startIcon={<VisibilityOutlinedIcon />}
                          variant="outlined"
                        >
                          Visualizar
                        </Button>
                      </Stack>
                    </CardContent>
                  </Card>
                ))
              )}
            </Stack>
          </>
        )}
      </Stack>

      <PaguaiCreateDialog
        onClose={() => setCreateOpen(false)}
        onSubmit={(values) => void submit(values)}
        open={createOpen}
        uploader={uploaderAdapter}
      />

      <DraftViewDialog
        companyName={selectedCompany?.companyName ?? "Sem empresa"}
        draft={selected}
        onClose={() => setViewOpen(false)}
        open={viewOpen}
        proofFileId={proofFileId}
      />
    </CompanyProvider>
  );
}

function DraftViewDialog({
  companyName,
  draft,
  onClose,
  open,
  proofFileId,
}: {
  companyName: string;
  draft: PaguaiDraft | null;
  onClose: () => void;
  open: boolean;
  proofFileId: string | null;
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
          <Typography component="p" variant="subtitle1">
            {draft.title}
          </Typography>
          <Typography>{draft.description}</Typography>
          <Typography color="text.secondary">
            Estado: {draft.state} · versão {draft.version}
          </Typography>
          <Typography>{formatAmount(draft.amount)}</Typography>
          <Typography>Vencimento: {formatDueDate(draft.dueDate)}</Typography>
          <Typography>
            Beneficiário: {draft.beneficiaryDisplayName} (informativo)
          </Typography>
          <Typography>
            Comprovante:{" "}
            {draft.proofRequirement === "required" ? "obrigatório" : "opcional"}
          </Typography>
          <Stack direction="row" spacing={1} sx={{ alignItems: "center" }}>
            <AttachFileOutlinedIcon color="action" fontSize="small" />
            <Typography>
              {proofFileId
                ? "Comprovante anexado nesta sessão."
                : "Nenhum comprovante anexado."}
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
