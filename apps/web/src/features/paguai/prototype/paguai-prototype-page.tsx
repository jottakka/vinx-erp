import { zodResolver } from "@hookform/resolvers/zod";
import {
  Button,
  Card,
  CardContent,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  FormHelperText,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  Tab,
  Tabs,
  TextField,
  Typography,
} from "@mui/material";
import { useRef, useState } from "react";
import { Controller, useForm } from "react-hook-form";
import { z } from "zod";
import type { DemoAttachmentState } from "../../prototypes/demo-attachment-field";
import { DemoAttachmentField } from "../../prototypes/demo-attachment-field";
import { DetailDrawer } from "../../prototypes/detail-drawer";
import { PrototypeNotice } from "../../prototypes/prototype-notice";
import type {
  PaguaiCompany,
  PaguaiRequest,
  PaguaiRequestStatus,
  PaguaiSection,
} from "./paguai-prototype-types";
import { usePaguaiPrototype } from "./use-paguai-prototype";

const draftSchema = z.object({
  amount: z.coerce.number().positive("Informe um valor maior que zero"),
  company: z.enum(["alfa", "beta"], { error: "Selecione uma empresa" }),
  description: z.string().trim().min(3, "Descreva a solicitação"),
});

const returnSchema = z.object({
  category: z.enum(["Dados incorretos", "Documento ausente", "Outro"], {
    error: "Selecione uma categoria",
  }),
  description: z.string().trim().min(3, "Descreva o motivo da devolução"),
});

type DraftInput = z.input<typeof draftSchema>;
type DraftValues = z.output<typeof draftSchema>;
type ReturnValues = z.infer<typeof returnSchema>;

const companyLabels: Record<PaguaiCompany, string> = {
  alfa: "Alfa",
  beta: "Beta",
};

const statusLabels: Record<PaguaiRequestStatus, string> = {
  approval: "Aguardando aprovação",
  conference: "Em conferência",
  draft: "Rascunho",
  finance: "Financeiro",
  paid: "Pago",
  returned: "Devolvida",
};

function RequestCard({
  onSelect,
  request,
}: {
  onSelect: () => void;
  request: PaguaiRequest;
}) {
  return (
    <Card component="article" variant="outlined">
      <CardContent>
        <Stack spacing={1}>
          <Typography component="h2" variant="h6">
            {request.description}
          </Typography>
          <Typography color="text.secondary">
            {companyLabels[request.company]} · {statusLabels[request.status]}
          </Typography>
          <Typography>
            {request.amount.toLocaleString("pt-BR", {
              currency: "BRL",
              style: "currency",
            })}
          </Typography>
          <Button onClick={onSelect} sx={{ alignSelf: "flex-start" }}>
            Ver detalhes
          </Button>
        </Stack>
      </CardContent>
    </Card>
  );
}

export function PaguaiPrototypePage() {
  const {
    actions,
    demoEvents,
    filters,
    policyAudience,
    requests,
    section,
    selected,
  } = usePaguaiPrototype();
  const [draftOpen, setDraftOpen] = useState(false);
  const [returnOpen, setReturnOpen] = useState(false);
  const [detailOpen, setDetailOpen] = useState(false);
  const [proofState, setProofState] = useState<DemoAttachmentState>("empty");
  const draftSequence = useRef(1);

  const draftForm = useForm<DraftInput, undefined, DraftValues>({
    defaultValues: {
      amount: 0,
      company: "" as DraftInput["company"],
      description: "",
    },
    resolver: zodResolver(draftSchema),
  });
  const returnForm = useForm<ReturnValues>({
    defaultValues: {
      category: "" as ReturnValues["category"],
      description: "",
    },
    resolver: zodResolver(returnSchema),
  });
  const conferenceRequest = requests.find(
    (request) => request.status === "conference",
  );
  const financeRequest = requests.find(
    (request) => request.status === "finance",
  );

  function openDraft() {
    draftForm.reset();
    setDraftOpen(true);
  }

  function saveDraft(values: DraftValues) {
    actions.createDraft({
      amount: values.amount,
      company: values.company,
      description: values.description,
      dueDate: "A definir",
      id: `paguai-draft-${draftSequence.current++}`,
      invoiceState: "metadata",
      proofRequired: false,
      status: "draft",
    });
    setDraftOpen(false);
  }

  function openReturn() {
    returnForm.reset();
    setReturnOpen(true);
  }

  function returnRequest(values: ReturnValues) {
    if (!conferenceRequest) {
      return;
    }
    actions.returnRequest(
      conferenceRequest.id,
      `${values.category}: ${values.description}`,
    );
    setReturnOpen(false);
  }

  function confirmPayment() {
    if (
      !financeRequest ||
      (financeRequest.proofRequired && proofState !== "ready")
    ) {
      return;
    }
    actions.confirmPayment(financeRequest.id);
  }

  return (
    <Stack spacing={3}>
      <Stack spacing={1}>
        <Typography component="h1" variant="h4">
          Paguai
        </Typography>
        <Typography color="text.secondary">
          Solicitações e fluxo financeiro para avaliação local.
        </Typography>
      </Stack>
      <PrototypeNotice />
      {demoEvents.map((event) => (
        <Typography key={event} role="status">
          {event}
        </Typography>
      ))}
      <Tabs
        onChange={(_, value: PaguaiSection) => actions.setSection(value)}
        value={section}
      >
        <Tab label="Solicitações" value="requests" />
        <Tab label="Conferência" value="conference" />
        <Tab label="Financeiro" value="finance" />
        <Tab label="Políticas" value="policies" />
      </Tabs>

      {section === "requests" ? (
        <Stack spacing={2}>
          <Button
            onClick={openDraft}
            sx={{ alignSelf: "flex-start" }}
            variant="contained"
          >
            Nova solicitação
          </Button>
          <Stack direction={{ sm: "row", xs: "column" }} spacing={2}>
            <FormControl fullWidth>
              <InputLabel id="paguai-filter-company-label">
                Filtrar por empresa
              </InputLabel>
              <Select
                label="Filtrar por empresa"
                labelId="paguai-filter-company-label"
                onChange={(event) =>
                  actions.setCompanyFilter(
                    event.target.value as PaguaiCompany | "all",
                  )
                }
                value={filters.company}
              >
                <MenuItem value="all">Todas</MenuItem>
                <MenuItem value="alfa">Alfa</MenuItem>
                <MenuItem value="beta">Beta</MenuItem>
              </Select>
            </FormControl>
            <FormControl fullWidth>
              <InputLabel id="paguai-filter-status-label">
                Filtrar por status
              </InputLabel>
              <Select
                label="Filtrar por status"
                labelId="paguai-filter-status-label"
                onChange={(event) =>
                  actions.setStatusFilter(
                    event.target.value as PaguaiRequestStatus | "all",
                  )
                }
                value={filters.status}
              >
                <MenuItem value="all">Todos</MenuItem>
                {Object.entries(statusLabels).map(([value, label]) => (
                  <MenuItem key={value} value={value}>
                    {label}
                  </MenuItem>
                ))}
              </Select>
            </FormControl>
          </Stack>
          {requests.map((request) => (
            <RequestCard
              key={request.id}
              onSelect={() => {
                actions.select(request.id);
                setDetailOpen(true);
              }}
              request={request}
            />
          ))}
        </Stack>
      ) : null}

      {section === "conference" ? (
        <Stack spacing={2}>
          <Typography component="h2" variant="h6">
            Conferência de solicitações
          </Typography>
          {conferenceRequest ? (
            <Card variant="outlined">
              <CardContent>
                <Stack spacing={2}>
                  <Typography>{conferenceRequest.description}</Typography>
                  <Typography color="text.secondary">
                    Confira os dados antes de devolver para ajustes.
                  </Typography>
                  <Button
                    onClick={openReturn}
                    sx={{ alignSelf: "flex-start" }}
                    variant="outlined"
                  >
                    Devolver para ajuste
                  </Button>
                </Stack>
              </CardContent>
            </Card>
          ) : (
            <Typography>Não há solicitações em conferência.</Typography>
          )}
        </Stack>
      ) : null}

      {section === "finance" ? (
        <Stack spacing={2}>
          <Typography component="h2" variant="h6">
            Fila financeira
          </Typography>
          {financeRequest ? (
            <Card variant="outlined">
              <CardContent>
                <Stack spacing={2}>
                  <Typography>{financeRequest.description}</Typography>
                  <Typography color="text.secondary">
                    Pagamento demonstrativo para{" "}
                    {companyLabels[financeRequest.company]}.
                  </Typography>
                  <DemoAttachmentField
                    label="Comprovante de pagamento"
                    onChange={setProofState}
                    required={financeRequest.proofRequired}
                    value={proofState}
                  />
                  <Button
                    disabled={
                      financeRequest.proofRequired && proofState !== "ready"
                    }
                    onClick={confirmPayment}
                    sx={{ alignSelf: "flex-start" }}
                    variant="contained"
                  >
                    Confirmar pagamento
                  </Button>
                </Stack>
              </CardContent>
            </Card>
          ) : (
            <Typography>Não há pagamentos pendentes.</Typography>
          )}
        </Stack>
      ) : null}

      {section === "policies" ? (
        <Stack spacing={2}>
          <Typography component="h2" variant="h6">
            Políticas locais
          </Typography>
          <FormControl fullWidth>
            <InputLabel id="paguai-policy-audience-label">
              Audiência da política
            </InputLabel>
            <Select
              label="Audiência da política"
              labelId="paguai-policy-audience-label"
              onChange={(event) =>
                actions.setPolicyAudience(
                  event.target.value as
                    | "Solicitantes"
                    | "Gestores"
                    | "Administradores",
                )
              }
              value={policyAudience}
            >
              <MenuItem value="Solicitantes">Solicitantes</MenuItem>
              <MenuItem value="Gestores">Gestores</MenuItem>
              <MenuItem value="Administradores">Administradores</MenuItem>
            </Select>
          </FormControl>
          <Typography>Audiência local: {policyAudience}</Typography>
          <Typography color="text.secondary">
            Simulação sem efeito real
          </Typography>
        </Stack>
      ) : null}

      <Dialog onClose={() => setDraftOpen(false)} open={draftOpen}>
        <DialogTitle>Nova solicitação</DialogTitle>
        <DialogContent>
          <Stack
            component="form"
            id="paguai-draft-form"
            onSubmit={draftForm.handleSubmit(saveDraft)}
            spacing={2}
            sx={{ pt: 1 }}
          >
            <Controller
              control={draftForm.control}
              name="company"
              render={({ field, fieldState }) => (
                <FormControl error={Boolean(fieldState.error)} fullWidth>
                  <InputLabel id="paguai-company-label">Empresa</InputLabel>
                  <Select
                    {...field}
                    inputProps={{
                      "aria-describedby": fieldState.error
                        ? "paguai-company-error"
                        : undefined,
                    }}
                    label="Empresa"
                    labelId="paguai-company-label"
                    value={field.value ?? ""}
                  >
                    <MenuItem value="alfa">Alfa</MenuItem>
                    <MenuItem value="beta">Beta</MenuItem>
                  </Select>
                  {fieldState.error ? (
                    <FormHelperText id="paguai-company-error">
                      {fieldState.error.message}
                    </FormHelperText>
                  ) : null}
                </FormControl>
              )}
            />
            <Controller
              control={draftForm.control}
              name="description"
              render={({ field, fieldState }) => (
                <TextField
                  {...field}
                  error={Boolean(fieldState.error)}
                  helperText={fieldState.error?.message}
                  label="Descrição"
                />
              )}
            />
            <Controller
              control={draftForm.control}
              name="amount"
              render={({ field, fieldState }) => (
                <TextField
                  {...field}
                  error={Boolean(fieldState.error)}
                  helperText={fieldState.error?.message}
                  label="Valor"
                  slotProps={{ htmlInput: { min: 0, step: "0.01" } }}
                  type="number"
                />
              )}
            />
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setDraftOpen(false)}>Cancelar</Button>
          <Button form="paguai-draft-form" type="submit" variant="contained">
            Salvar rascunho
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog onClose={() => setReturnOpen(false)} open={returnOpen}>
        <DialogTitle>Devolver solicitação</DialogTitle>
        <DialogContent>
          <Stack
            component="form"
            id="paguai-return-form"
            onSubmit={returnForm.handleSubmit(returnRequest)}
            spacing={2}
            sx={{ pt: 1 }}
          >
            <Controller
              control={returnForm.control}
              name="category"
              render={({ field, fieldState }) => (
                <FormControl error={Boolean(fieldState.error)} fullWidth>
                  <InputLabel id="paguai-return-category-label">
                    Categoria
                  </InputLabel>
                  <Select
                    {...field}
                    inputProps={{
                      "aria-describedby": fieldState.error
                        ? "paguai-return-category-error"
                        : undefined,
                    }}
                    label="Categoria"
                    labelId="paguai-return-category-label"
                    value={field.value ?? ""}
                  >
                    <MenuItem value="Dados incorretos">
                      Dados incorretos
                    </MenuItem>
                    <MenuItem value="Documento ausente">
                      Documento ausente
                    </MenuItem>
                    <MenuItem value="Outro">Outro</MenuItem>
                  </Select>
                  {fieldState.error ? (
                    <FormHelperText id="paguai-return-category-error">
                      {fieldState.error.message}
                    </FormHelperText>
                  ) : null}
                </FormControl>
              )}
            />
            <Controller
              control={returnForm.control}
              name="description"
              render={({ field, fieldState }) => (
                <TextField
                  {...field}
                  error={Boolean(fieldState.error)}
                  helperText={fieldState.error?.message}
                  label="Descrição da devolução"
                  multiline
                />
              )}
            />
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setReturnOpen(false)}>Cancelar</Button>
          <Button form="paguai-return-form" type="submit" variant="contained">
            Confirmar devolução
          </Button>
        </DialogActions>
      </Dialog>

      <DetailDrawer
        onClose={() => {
          actions.select(null);
          setDetailOpen(false);
        }}
        open={detailOpen && Boolean(selected)}
        title={selected?.description ?? "Detalhes da solicitação"}
      >
        {selected ? (
          <Stack spacing={1}>
            <Typography>{companyLabels[selected.company]}</Typography>
            <Typography>{statusLabels[selected.status]}</Typography>
            <Typography>Vencimento: {selected.dueDate}</Typography>
          </Stack>
        ) : null}
      </DetailDrawer>
    </Stack>
  );
}
