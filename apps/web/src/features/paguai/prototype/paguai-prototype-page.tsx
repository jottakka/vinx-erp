import { Stack, Tab, Tabs, Typography } from "@mui/material";
import { useEffect, useRef, useState } from "react";
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
  const [draftOpen, setDraftOpen] = useState(false);
  const [returnRequestId, setReturnRequestId] = useState<string | null>(null);
  const [returnDialogExited, setReturnDialogExited] = useState(true);
  const [detailOpen, setDetailOpen] = useState(false);
  const [proofState, setProofState] = useState<DemoAttachmentState>("empty");
  const draftSequence = useRef(1);
  const returnDialogOpenerRef = useRef<HTMLButtonElement | null>(null);
  const returnedRequestFocusId = useRef<string | null>(null);
  const selectedRequestRef = useRef<HTMLElement | null>(null);
  const financeRequest = financeRequests[0];

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
      ) : null}

      {section === "conference" ? (
        <PaguaiConferencePanel
          onReturn={openReturnDialog}
          onSelect={actions.select}
          requests={conferenceRequests}
          selected={selected}
          selectedRequestRef={selectedRequestRef}
        />
      ) : null}

      {section === "finance" ? (
        <PaguaiFinancePanel
          onConfirm={confirmPayment}
          onProofChange={setProofState}
          proofState={proofState}
          request={financeRequest}
        />
      ) : null}

      {section === "policies" ? (
        <PaguaiPoliciesPanel
          audience={policyAudience}
          onAudienceChange={actions.setPolicyAudience}
        />
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
