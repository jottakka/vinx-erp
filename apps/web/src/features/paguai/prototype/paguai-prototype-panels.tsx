import {
  Alert,
  Button,
  Card,
  CardContent,
  FormControl,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  Typography,
} from "@mui/material";
import type { Ref } from "react";
import type { DemoAttachmentState } from "../../prototypes/demo-attachment-field";
import { DemoAttachmentField } from "../../prototypes/demo-attachment-field";
import type {
  PaguaiCompany,
  PaguaiFilters,
  PaguaiRequest,
  PaguaiRequestStatus,
  PaguaiState,
} from "./paguai-prototype-types";

export const paguaiCompanyLabels: Record<PaguaiCompany, string> = {
  alfa: "Alfa",
  beta: "Beta",
};

export const paguaiStatusLabels: Record<PaguaiRequestStatus, string> = {
  approval: "Aguardando aprovação",
  conference: "Em conferência",
  draft: "Rascunho",
  finance: "Financeiro",
  paid: "Pago",
  returned: "Devolvida",
};

type RequestsPanelProps = {
  filters: PaguaiFilters;
  onCompanyFilter: (company: PaguaiFilters["company"]) => void;
  onNewRequest: () => void;
  onSelect: (id: string) => void;
  onStatusFilter: (status: PaguaiFilters["status"]) => void;
  requests: readonly PaguaiRequest[];
};

export function PaguaiRequestsPanel({
  filters,
  onCompanyFilter,
  onNewRequest,
  onSelect,
  onStatusFilter,
  requests,
}: RequestsPanelProps) {
  return (
    <Stack spacing={2}>
      <Button
        onClick={onNewRequest}
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
              onCompanyFilter(event.target.value as PaguaiFilters["company"])
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
              onStatusFilter(event.target.value as PaguaiFilters["status"])
            }
            value={filters.status}
          >
            <MenuItem value="all">Todos</MenuItem>
            {Object.entries(paguaiStatusLabels).map(([value, label]) => (
              <MenuItem key={value} value={value}>
                {label}
              </MenuItem>
            ))}
          </Select>
        </FormControl>
      </Stack>
      {requests.length ? null : (
        <Alert severity="info">
          Nenhuma solicitação corresponde aos filtros escolhidos.
        </Alert>
      )}
      {requests.map((request) => (
        <Card component="article" key={request.id} variant="outlined">
          <CardContent>
            <Stack spacing={1}>
              <Typography component="h2" variant="h6">
                {request.description}
              </Typography>
              <Typography color="text.secondary">
                {paguaiCompanyLabels[request.company]} ·{" "}
                {paguaiStatusLabels[request.status]}
              </Typography>
              <Typography>
                {request.amount.toLocaleString("pt-BR", {
                  currency: "BRL",
                  style: "currency",
                })}
              </Typography>
              <Button
                onClick={() => onSelect(request.id)}
                sx={{ alignSelf: "flex-start" }}
              >
                Ver detalhes
              </Button>
            </Stack>
          </CardContent>
        </Card>
      ))}
    </Stack>
  );
}

type ConferencePanelProps = {
  onReturn: (id: string, opener: HTMLButtonElement) => void;
  onSelect: (id: string) => void;
  requests: readonly PaguaiRequest[];
  selected?: PaguaiRequest;
  selectedRequestRef: Ref<HTMLElement>;
};

export function PaguaiConferencePanel({
  onReturn,
  onSelect,
  requests,
  selected,
  selectedRequestRef,
}: ConferencePanelProps) {
  const conferenceSelection =
    selected?.status === "conference" || selected?.status === "returned"
      ? selected
      : undefined;

  return (
    <Stack spacing={2}>
      <Typography component="h2" variant="h6">
        Conferência de solicitações
      </Typography>
      {requests.length > 0 ? (
        <Stack
          component="ul"
          spacing={1}
          sx={{ listStyle: "none", m: 0, p: 0 }}
        >
          {requests.map((request) => (
            <Card component="li" key={request.id} variant="outlined">
              <CardContent>
                <Stack spacing={1}>
                  <Typography>{request.description}</Typography>
                  <Typography>
                    Empresa: {paguaiCompanyLabels[request.company]}
                  </Typography>
                  <Button
                    aria-label={`Selecionar ${request.description} da empresa ${paguaiCompanyLabels[request.company]}`}
                    onClick={() => onSelect(request.id)}
                    sx={{ alignSelf: "flex-start" }}
                  >
                    Selecionar
                  </Button>
                </Stack>
              </CardContent>
            </Card>
          ))}
        </Stack>
      ) : (
        <Alert severity="info">Não há solicitações em conferência.</Alert>
      )}
      {conferenceSelection ? (
        <Card
          aria-label="Solicitação selecionada"
          component="section"
          ref={selectedRequestRef}
          tabIndex={-1}
          variant="outlined"
        >
          <CardContent>
            <Stack spacing={1}>
              <Typography component="h3" variant="subtitle1">
                {conferenceSelection.description}
              </Typography>
              <Typography>
                Empresa: {paguaiCompanyLabels[conferenceSelection.company]}
              </Typography>
              {conferenceSelection.returnReason ? (
                <>
                  <Typography>
                    Categoria: {conferenceSelection.returnReason.category}
                  </Typography>
                  <Typography>
                    Motivo: {conferenceSelection.returnReason.description}
                  </Typography>
                </>
              ) : null}
              {conferenceSelection.status === "conference" ? (
                <Button
                  aria-label={`Devolver ${conferenceSelection.description} para ajuste`}
                  onClick={(event) =>
                    onReturn(conferenceSelection.id, event.currentTarget)
                  }
                  sx={{ alignSelf: "flex-start" }}
                  variant="outlined"
                >
                  Devolver para ajuste
                </Button>
              ) : null}
            </Stack>
          </CardContent>
        </Card>
      ) : (
        <Typography color="text.secondary" sx={{ fontStyle: "italic" }}>
          Selecione uma solicitação para conferir.
        </Typography>
      )}
    </Stack>
  );
}

type FinancePanelProps = {
  onConfirm: () => void;
  onProofChange: (state: DemoAttachmentState) => void;
  proofState: DemoAttachmentState;
  request?: PaguaiRequest;
};

export function PaguaiFinancePanel({
  onConfirm,
  onProofChange,
  proofState,
  request,
}: FinancePanelProps) {
  return (
    <Stack spacing={2}>
      <Typography component="h2" variant="h6">
        Fila financeira
      </Typography>
      {request ? (
        <Card variant="outlined">
          <CardContent>
            <Stack spacing={2}>
              <Typography>{request.description}</Typography>
              <Typography color="text.secondary">
                Pagamento demonstrativo para{" "}
                {paguaiCompanyLabels[request.company]}.
              </Typography>
              <DemoAttachmentField
                label="Comprovante de pagamento"
                onChange={onProofChange}
                required={request.proofRequired}
                value={proofState}
              />
              <Button
                disabled={request.proofRequired && proofState !== "ready"}
                onClick={onConfirm}
                sx={{ alignSelf: "flex-start" }}
                variant="contained"
              >
                Confirmar pagamento
              </Button>
            </Stack>
          </CardContent>
        </Card>
      ) : (
        <Alert severity="info">Não há pagamentos pendentes.</Alert>
      )}
    </Stack>
  );
}

type PoliciesPanelProps = {
  audience: PaguaiState["policyAudience"];
  onAudienceChange: (audience: PaguaiState["policyAudience"]) => void;
};

export function PaguaiPoliciesPanel({
  audience,
  onAudienceChange,
}: PoliciesPanelProps) {
  return (
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
            onAudienceChange(
              event.target.value as PaguaiState["policyAudience"],
            )
          }
          value={audience}
        >
          <MenuItem value="Solicitantes">Solicitantes</MenuItem>
          <MenuItem value="Gestores">Gestores</MenuItem>
          <MenuItem value="Administradores">Administradores</MenuItem>
        </Select>
      </FormControl>
      <Typography>Audiência local: {audience}</Typography>
      <Typography color="text.secondary">Simulação sem efeito real</Typography>
    </Stack>
  );
}
