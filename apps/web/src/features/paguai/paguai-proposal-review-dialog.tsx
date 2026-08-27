import { Button, Stack, Typography } from "@mui/material";
import { CompanyIdentity } from "../../shared/ui/company-identity";
import { EntityDialog } from "../../shared/ui/entity-dialog";
import type { PaguaiProposal } from "./paguai-draft-client";
import { formatAmount, formatDueDate } from "./paguai-format";

export function PaguaiProposalReviewDialog({
  companyName,
  onClose,
  onConfirm,
  onReject,
  open,
  proposal,
  submitting,
}: {
  companyName: string;
  onClose: () => void;
  onConfirm: () => void;
  onReject: () => void;
  open: boolean;
  proposal: PaguaiProposal | null;
  submitting: boolean;
}) {
  return (
    <EntityDialog
      description="Revise os dados extraídos antes de criar a solicitação. Os valores não podem ser alterados aqui."
      onClose={onClose}
      open={open}
      title="Revisar proposta"
    >
      {proposal ? (
        <Stack spacing={1.5}>
          <CompanyIdentity
            companyId={proposal.companyId}
            companyName={companyName}
          />
          <Typography component="p" variant="subtitle1">
            {proposal.title}
          </Typography>
          <Typography>{proposal.description}</Typography>
          <Typography>{formatAmount(proposal.amount)}</Typography>
          <Typography>Pagar até: {formatDueDate(proposal.dueDate)}</Typography>
          <Typography>
            Quem receberá: {proposal.beneficiaryDisplayName}
          </Typography>
          <Typography>
            Comprovante de pagamento:{" "}
            {proposal.proofRequirement === "required"
              ? "obrigatório"
              : "opcional"}
          </Typography>
          <Stack direction={{ sm: "row", xs: "column" }} spacing={1}>
            <Button disabled={submitting} onClick={onReject} variant="outlined">
              Rejeitar proposta
            </Button>
            <Button
              disabled={submitting}
              onClick={onConfirm}
              variant="contained"
            >
              Confirmar solicitação
            </Button>
          </Stack>
        </Stack>
      ) : null}
    </EntityDialog>
  );
}
