import { Fade, Stack, Tab, Tabs, Typography } from "@mui/material";
import { useEffect, useRef, useState } from "react";
import { useFeedback } from "../../../shared/ui/feedback-provider";
import type { DemoAttachmentState } from "../../prototypes/demo-attachment-field";
import { DetailDrawer } from "../../prototypes/detail-drawer";
import { PrototypeNotice } from "../../prototypes/prototype-notice";
import {
  PaguaiDraftDialog,
  type PaguaiDraftValues,
  PaguaiReturnDialog,
  type PaguaiReturnValues,
} from "./paguai-prototype-dialogs";
import {
  PaguaiConferencePanel,
  PaguaiFinancePanel,
  PaguaiPoliciesPanel,
  PaguaiRequestsPanel,
  paguaiCompanyLabels,
  paguaiStatusLabels,
} from "./paguai-prototype-panels";
import type { PaguaiSection } from "./paguai-prototype-types";
import { usePaguaiPrototype } from "./use-paguai-prototype";

export function PaguaiPrototypePage() {
  const {
    actions,
    conferenceRequests,
    demoEvents,
    filters,
    financeRequests,
    policyAudience,
    requests,
    section,
    selected,
  } = usePaguaiPrototype();
  const { notify } = useFeedback();
  const [draftOpen, setDraftOpen] = useState(false);
  const [returnRequestId, setReturnRequestId] = useState<string | null>(null);
  const [returnDialogExited, setReturnDialogExited] = useState(true);
  const [detailOpen, setDetailOpen] = useState(false);
  const [proofState, setProofState] = useState<DemoAttachmentState>("empty");
  const notifiedEventIds = useRef<ReadonlySet<string>>(new Set());
  const draftSequence = useRef(1);
  const returnDialogOpenerRef = useRef<HTMLButtonElement | null>(null);
  const returnedRequestFocusId = useRef<string | null>(null);
  const selectedRequestRef = useRef<HTMLElement | null>(null);
  const financeRequest = financeRequests[0];

  useEffect(() => {
    const pendingEvents = demoEvents.filter(
      (event) => !notifiedEventIds.current.has(event.id),
    );

    if (!pendingEvents.length) {
      return;
    }

    notifiedEventIds.current = new Set([
      ...notifiedEventIds.current,
      ...pendingEvents.map((event) => event.id),
    ]);
    notify(pendingEvents[pendingEvents.length - 1].message);
  }, [demoEvents, notify]);

  useEffect(() => {
    const requestId = returnedRequestFocusId.current;
    const selectedRequest = selectedRequestRef.current;

    if (!returnDialogExited) {
      return;
    }

    if (requestId) {
      if (
        selected?.id !== requestId ||
        selected.status !== "returned" ||
        !selectedRequest?.isConnected
      ) {
        return;
      }

      selectedRequest.focus();
      returnedRequestFocusId.current = null;
      returnDialogOpenerRef.current = null;
      return;
    }

    const opener = returnDialogOpenerRef.current;
    returnDialogOpenerRef.current = null;
    if (opener?.isConnected) {
      opener.focus();
    }
  }, [returnDialogExited, selected]);

  function saveDraft(values: PaguaiDraftValues) {
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

  function returnRequest(values: PaguaiReturnValues) {
    if (!returnRequestId) {
      return;
    }
    returnedRequestFocusId.current = returnRequestId;
    actions.returnRequest(returnRequestId, values);
    setReturnRequestId(null);
  }

  function openReturnDialog(id: string, opener: HTMLButtonElement) {
    returnDialogOpenerRef.current = opener;
    returnedRequestFocusId.current = null;
    setReturnDialogExited(false);
    setReturnRequestId(id);
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
      <Tabs
        aria-label="Seções do Paguai"
        onChange={(_, value: PaguaiSection) => actions.setSection(value)}
        value={section}
      >
        <Tab
          aria-controls="paguai-panel-requests"
          id="paguai-tab-requests"
          label="Solicitações"
          value="requests"
        />
        <Tab
          aria-controls="paguai-panel-conference"
          id="paguai-tab-conference"
          label="Conferência"
          value="conference"
        />
        <Tab
          aria-controls="paguai-panel-finance"
          id="paguai-tab-finance"
          label="Financeiro"
          value="finance"
        />
        <Tab
          aria-controls="paguai-panel-policies"
          id="paguai-tab-policies"
          label="Políticas"
          value="policies"
        />
      </Tabs>

      {section === "requests" ? (
        <Fade appear in key="requests">
          <Stack
            aria-labelledby="paguai-tab-requests"
            id="paguai-panel-requests"
            role="tabpanel"
          >
            <PaguaiRequestsPanel
              filters={filters}
              onCompanyFilter={actions.setCompanyFilter}
              onNewRequest={() => setDraftOpen(true)}
              onSelect={(id) => {
                actions.select(id);
                setDetailOpen(true);
              }}
              onStatusFilter={actions.setStatusFilter}
              requests={requests}
            />
          </Stack>
        </Fade>
      ) : null}

      {section === "conference" ? (
        <Fade appear in key="conference">
          <Stack
            aria-labelledby="paguai-tab-conference"
            id="paguai-panel-conference"
            role="tabpanel"
          >
            <PaguaiConferencePanel
              onReturn={openReturnDialog}
              onSelect={actions.select}
              requests={conferenceRequests}
              selected={selected}
              selectedRequestRef={selectedRequestRef}
            />
          </Stack>
        </Fade>
      ) : null}

      {section === "finance" ? (
        <Fade appear in key="finance">
          <Stack
            aria-labelledby="paguai-tab-finance"
            id="paguai-panel-finance"
            role="tabpanel"
          >
            <PaguaiFinancePanel
              onConfirm={confirmPayment}
              onProofChange={setProofState}
              proofState={proofState}
              request={financeRequest}
            />
          </Stack>
        </Fade>
      ) : null}

      {section === "policies" ? (
        <Fade appear in key="policies">
          <Stack
            aria-labelledby="paguai-tab-policies"
            id="paguai-panel-policies"
            role="tabpanel"
          >
            <PaguaiPoliciesPanel
              audience={policyAudience}
              onAudienceChange={actions.setPolicyAudience}
            />
          </Stack>
        </Fade>
      ) : null}

      <PaguaiDraftDialog
        onClose={() => setDraftOpen(false)}
        onSubmit={saveDraft}
        open={draftOpen}
      />
      <PaguaiReturnDialog
        onClose={() => setReturnRequestId(null)}
        onSubmit={returnRequest}
        onTransitionExited={() => setReturnDialogExited(true)}
        open={returnRequestId !== null}
      />

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
            <Typography>{paguaiCompanyLabels[selected.company]}</Typography>
            <Typography>{paguaiStatusLabels[selected.status]}</Typography>
            <Typography>Vencimento: {selected.dueDate}</Typography>
          </Stack>
        ) : null}
      </DetailDrawer>
    </Stack>
  );
}
