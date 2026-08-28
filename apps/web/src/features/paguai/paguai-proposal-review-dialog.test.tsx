import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { PaguaiProposalReviewDialog } from "./paguai-proposal-review-dialog";

const proposal = {
  id: "44444444-4444-4444-8444-444444444444",
  companyId: "11111111-1111-4111-8111-111111111111",
  title: "Papel",
  description: "Papel para impressora",
  amount: "1250.00",
  currency: "BRL" as const,
  dueDate: "2026-09-01",
  beneficiaryDisplayName: "Fornecedor Demo",
  proofRequirement: "required" as const,
  invoiceFileId: "55555555-5555-4555-8555-555555555555",
  confidence: 0.98,
  sourceReferences: [{ field: "amount", source: "page:1" }],
  state: "pending" as const,
  createdAt: "2026-08-26T12:00:00.000+00:00",
  expiresAt: "2026-08-26T12:15:00.000+00:00",
};

describe("PaguaiProposalReviewDialog", () => {
  it("shows only persisted typed review fields and fixed confirmation actions", () => {
    render(
      <VinxThemeProvider>
        <PaguaiProposalReviewDialog
          companyName="Alfa"
          onClose={vi.fn()}
          onConfirm={vi.fn()}
          onReject={vi.fn()}
          open
          proposal={proposal}
          submitting={false}
        />
      </VinxThemeProvider>,
    );

    expect(screen.getByText("Papel para impressora")).toBeVisible();
    expect(screen.getByText("R$ 1.250,00")).toBeVisible();
    expect(screen.getByText("Pagar até: 01/09/2026")).toBeVisible();
    expect(
      screen.getByRole("button", { name: "Confirmar solicitação" }),
    ).toBeVisible();
    expect(
      screen.getByRole("button", { name: "Rejeitar proposta" }),
    ).toBeVisible();
    expect(screen.queryByRole("textbox")).toBeNull();
    expect(screen.queryByText("page:1")).toBeNull();
    expect(
      screen.queryByText("55555555-5555-4555-8555-555555555555"),
    ).toBeNull();
  });
});
